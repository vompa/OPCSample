using System.Security.Claims;
using OPCClient.Storage;
using Serilog;

namespace OPCClient.Security;

/// <summary>
/// Zentrale Stelle für das Audit-Log: eine Logzeile (Serilog) und ein persistenter Eintrag je Zugriff.
/// Ein Fehler beim Schreiben bricht die Anfrage nicht ab, wird aber als Fehler geloggt.
/// </summary>
public sealed class AuditLog(SqliteAuditStore store)
{
    public const int MaxTextLength = 300;
    private const string Unknown = "unbekannt";

    public void Record(string channel, ClaimsPrincipal? user, string action, string? arguments, string outcome, string? error = null)
    {
        var client = user?.Identity?.Name ?? Unknown;
        var role = user?.FindFirst(ClaimTypes.Role)?.Value ?? Unknown;
        var entry = new AuditEntry(DateTime.UtcNow, client, role, channel, action,
            Shorten(arguments), outcome, Shorten(error));

        if (outcome == AuditOutcomes.Ok)
            Log.Information("Audit: {Channel} Client {Client} ({Role}) ruft {Action} auf: {Arguments}",
                channel, client, role, action, entry.Arguments);
        else
            Log.Warning("Audit: {Channel} Client {Client} ({Role}) {Action} {Outcome}: {Error} {Arguments}",
                channel, client, role, action, outcome, entry.Error, entry.Arguments);

        try { store.Add(entry); }
        catch (Exception ex) { Log.Error(ex, "Audit-Eintrag konnte nicht gespeichert werden ({Action})", action); }
    }

    private static string? Shorten(string? text) =>
        text is { Length: > MaxTextLength } ? text[..MaxTextLength] + "..." : text;
}
