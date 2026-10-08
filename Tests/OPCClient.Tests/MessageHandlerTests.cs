using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OPCClient.Opc;

namespace OPCClient.Tests;

public class MessageHandlerTests
{
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static WebhookMessageHandler Webhook(CapturingHandler http) =>
        new(new FakeFactory(http), Options.Create(new SinkOptions { Type = SinkOptions.Webhook, WebhookUrl = "https://example.org/hook" }));

    [Fact]
    public async Task Webhook_posts_json_with_a_stable_idempotency_key()
    {
        var http = new CapturingHandler(HttpStatusCode.OK);
        var message = new OpcMessage("Presse-1", "Temperatur", 21.5, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), Id: 42);

        await Webhook(http).HandleAsync(message, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, http.Request!.Method);
        Assert.Equal("https://example.org/hook", http.Request.RequestUri!.ToString());
        Assert.Equal("Presse-1-42", http.Request.Headers.GetValues("Idempotency-Key").Single());
        var body = JsonDocument.Parse(http.Body!).RootElement;
        Assert.Equal("Temperatur", body.GetProperty("name").GetString());
        Assert.Equal(21.5, body.GetProperty("value").GetDouble());
        Assert.Equal(42, body.GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Webhook_fails_on_non_success_status_so_the_message_is_retried(HttpStatusCode status)
    {
        var http = new CapturingHandler(status);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Webhook(http).HandleAsync(new OpcMessage("M", "T", 1, DateTime.UtcNow, 1), CancellationToken.None));
    }

    [Fact]
    public async Task Logging_handler_never_throws() =>
        await new LoggingMessageHandler().HandleAsync(new OpcMessage("M", "T", 1, DateTime.UtcNow), CancellationToken.None);

    [Theory]
    [InlineData("Log", "", true)]
    [InlineData("Webhook", "https://example.org/hook", true)]
    [InlineData("Webhook", "http://localhost:8080/hook", true)]
    [InlineData("Webhook", "http://127.0.0.1:8080/hook", true)]
    [InlineData("Webhook", "http://example.org/hook", false)]   // unverschlüsselt nach außen
    [InlineData("Webhook", "", false)]
    [InlineData("Webhook", "kein-url", false)]
    [InlineData("Webhook", "ftp://example.org/hook", false)]
    [InlineData("Kafka", "", false)]
    public void Sink_configuration_is_validated(string type, string url, bool expectedValid)
    {
        var valid = new SinkOptions { Type = type, WebhookUrl = url }.IsValid(out var error);

        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedValid, error.Length == 0);
    }
}
