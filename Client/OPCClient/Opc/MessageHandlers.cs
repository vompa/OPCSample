using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Serilog;

namespace OPCClient.Opc;

/// <summary>Verarbeitet jede Nachricht (neueste zuerst). Muss idempotent sein und das Token weiterreichen.</summary>
public interface IMessageHandler
{
    Task HandleAsync(OpcMessage message, CancellationToken ct);
}

/// <summary>Konfiguration unter <c>Opc:Sink</c>.</summary>
public sealed class SinkOptions
{
    public const string SectionName = "Opc:Sink";
    public const string Log = "Log";
    public const string Webhook = "Webhook";

    public string Type { get; set; } = Log;
    public string WebhookUrl { get; set; } = "";

    /// <summary>Prüft die Konfiguration beim Start. Webhooks sind nur über HTTPS erlaubt (außer Loopback).</summary>
    public bool IsValid(out string error)
    {
        error = "";
        if (Type == Log) return true;

        if (Type != Webhook)
        {
            error = $"Opc:Sink:Type '{Type}' ist unbekannt. Erlaubt: {Log}, {Webhook}.";
            return false;
        }

        if (!Uri.TryCreate(WebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "Opc:Sink:WebhookUrl muss eine absolute http(s)-URL sein.";
            return false;
        }

        if (uri.Scheme == "http" && !uri.IsLoopback)
        {
            error = "Opc:Sink:WebhookUrl muss HTTPS verwenden (HTTP ist nur für localhost erlaubt).";
            return false;
        }

        return true;
    }
}

/// <summary>Standard: schreibt jeden Wert ins Log. Ersetzt den früheren Platzhalter im Hosted Service.</summary>
public sealed class LoggingMessageHandler : IMessageHandler
{
    public Task HandleAsync(OpcMessage message, CancellationToken ct)
    {
        Log.ForContext("Machine", message.Machine).Information("{Name} = {Value}", message.Name, message.Value);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Leitet jede Nachricht per HTTP-POST an einen Webhook weiter. Ein Statuscode außerhalb von 2xx löst den
/// normalen Retry (mit Backoff) aus. Weil die Zustellung at-least-once ist, trägt jede Anfrage einen stabilen
/// <c>Idempotency-Key</c> (Maschine + Datenbank-Id), an dem der Empfänger Duplikate erkennen kann.
/// </summary>
public sealed class WebhookMessageHandler(IHttpClientFactory clients, IOptions<SinkOptions> options) : IMessageHandler
{
    public const string ClientName = "sink";
    public const string IdempotencyHeader = "Idempotency-Key";

    public async Task HandleAsync(OpcMessage message, CancellationToken ct)
    {
        var client = clients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.WebhookUrl)
        {
            Content = JsonContent.Create(new
            {
                id = message.Id,
                machine = message.Machine,
                name = message.Name,
                value = message.Value,
                timestamp = message.Timestamp.ToUniversalTime(),
            }),
        };
        request.Headers.TryAddWithoutValidation(IdempotencyHeader, $"{message.Machine}-{message.Id}");

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
