using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;

namespace OPCClient.Security;

public static class RateLimitPolicies
{
    /// <summary>Strengere Grenze für den MCP-Endpunkt (KI-Agenten).</summary>
    public const string Mcp = "mcp";
}

/// <summary>Antwort bei überschrittenem Limit: 429 mit <c>Retry-After</c> und verständlichem Text, dazu eine Warnung im Log.</summary>
public static class RateLimitRejection
{
    private const int FallbackRetryAfterSeconds = 60;

    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max((int)Math.Ceiling(delay.TotalSeconds), 1)
            : FallbackRetryAfterSeconds;

        var http = context.HttpContext;
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        http.Response.ContentType = "text/plain; charset=utf-8";

        Log.Warning("Rate Limit überschritten: Client {Client} auf {Path}, erneut möglich in {Seconds} s",
            http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            http.Request.Path, retryAfter);

        await http.Response.WriteAsync(
            $"Zu viele Anfragen. Bitte in {retryAfter} Sekunden erneut versuchen.", cancellationToken);
    }
}
