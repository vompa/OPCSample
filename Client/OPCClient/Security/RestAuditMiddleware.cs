using OPCClient.Storage;

namespace OPCClient.Security;

/// <summary>Markiert einen REST-Endpunkt als auditpflichtig (schreibender Zugriff); <paramref name="Action"/> ist der Name im Audit-Log.</summary>
public sealed record AuditedEndpoint(string Action);

/// <summary>
/// Schreibt für jeden Aufruf eines auditpflichtigen Endpunkts einen Eintrag, auch wenn die Autorisierung
/// ihn mit 403 abweist. Läuft deshalb vor der Autorisierung. Anfragen ohne gültigen Key (401) kennen keinen Client
/// und werden nicht protokolliert (der Key-Handler loggt sie als Warnung).
/// </summary>
public sealed class RestAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuditLog audit)
    {
        var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<AuditedEndpoint>();
        if (endpoint is null)
        {
            await next(context);
            return;
        }

        var arguments = string.Join(" ", context.Request.RouteValues.Select(v => $"{v.Key}={v.Value}"));
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            audit.Record(AuditChannels.Rest, context.User, endpoint.Action, arguments, AuditOutcomes.Error, ex.Message);
            throw;
        }

        var status = context.Response.StatusCode;
        if (status == StatusCodes.Status401Unauthorized) return;

        var (outcome, error) = Classify(status);
        audit.Record(AuditChannels.Rest, context.User, endpoint.Action, arguments, outcome, error);
    }

    private static (string Outcome, string? Error) Classify(int status) => status switch
    {
        StatusCodes.Status403Forbidden => (AuditOutcomes.Denied, "HTTP 403: keine Berechtigung"),
        >= 400 => (AuditOutcomes.Error, $"HTTP {status}"),
        _ => (AuditOutcomes.Ok, null),
    };
}
