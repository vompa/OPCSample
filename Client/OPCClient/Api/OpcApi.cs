using Microsoft.AspNetCore.Mvc;
using OPCClient.Opc;
using OPCClient.Security;
using OPCClient.Storage;

namespace OPCClient.Api;

public static class OpcApi
{
    public static void MapOpcApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Öffentlich, aber ohne Details: "degraded", solange eine konfigurierte Maschine nicht verbunden ist.
        // Bewusst HTTP 200 (Liveness): ein fehlender OPC-Server soll den Dienst nicht neu starten lassen.
        api.MapGet("/health", (MachineRegistry reg) =>
                Results.Ok(new { status = reg.All.All(m => m.IsConnected) ? "ok" : "degraded", time = DateTime.UtcNow }))
           .AllowAnonymous()
           .WithTags("System").WithSummary("Lebenszeichen (ok / degraded)");

        api.MapGet("/machines", (MachineRegistry reg, SqliteMessageStore s) =>
                reg.All.Select(m => new MachineDto(m.Name, m.IsConnected, s.Stats(m.Name))))
           .WithTags("Maschinen").WithSummary("Alle Maschinen mit Verbindungsstatus und Nachrichtenzahlen");

        api.MapGet("/machines/{machine}/latest", (string machine, MachineRegistry reg, SqliteMessageStore s) =>
                reg.Get(machine) is null ? Results.NotFound() : Results.Ok(s.Latest(machine)))
           .WithTags("Maschinen").WithSummary("Aktuellster Wert je Variable");

        api.MapGet("/messages", ([AsParameters] MessageFilter f, SqliteMessageStore s) => s.Query(f))
           .WithTags("Nachrichten")
           .WithSummary("Nachrichten filtern (machine, name, state, from, to, limit, offset), neueste zuerst");

        api.MapGet("/messages/{id:long}", (long id, SqliteMessageStore s) =>
                s.Get(id) is { } m ? Results.Ok(m) : Results.NotFound())
           .WithTags("Nachrichten").WithSummary("Einzelne Nachricht");

        api.MapGet("/alarms", ([AsParameters] AlarmFilter f, SqliteAlarmStore alarms) => alarms.Query(f))
           .WithTags("Alarme")
           .WithSummary("Alarme filtern (machine, name, from, to, activeOnly, limit, offset), neueste zuerst; from/to = Auslösezeitpunkt");

        api.MapGet("/machines/{machine}/statistics", (string machine, string name, DateTime? from, DateTime? to,
                int? bucketMinutes, SqliteStatisticsReader statistics) => Statistics(statistics, machine, name, from, to, bucketMinutes))
           .WithTags("Maschinen")
           .WithSummary("Statistik einer numerischen Variable über Wertänderungen (nicht zeitgewichtet); Standard: letzte Stunde, höchstens 31 Tage");

        api.MapGet("/audit", ([AsParameters] AuditFilter f, SqliteAuditStore audit) =>
                f.Outcome is not null && !AuditOutcomes.All.Contains(f.Outcome)
                    ? Results.Problem($"Unbekanntes Ergebnis '{f.Outcome}'. Gültig: {string.Join(", ", AuditOutcomes.All)}.", statusCode: 400)
                    : Results.Ok(audit.Query(f)))
           .RequireAuthorization(Policies.Writer)
           .WithTags("Audit")
           .WithSummary("Audit-Log filtern (client, action, outcome, from, to, limit, offset), neueste zuerst (Rolle writer)");

        api.MapPost("/messages/{id:long}/retry", (long id, SqliteMessageStore s, MachineRegistry reg) =>
            {
                var machine = s.Requeue(id);
                if (machine is null) return Results.NotFound("Nicht gefunden oder nicht fehlgeschlagen");
                reg.Get(machine)?.Wake();
                return Results.Accepted();
            })
           .RequireAuthorization(Policies.Writer)
           .WithMetadata(new AuditedEndpoint("retry_message"))
           .WithTags("Nachrichten").WithSummary("Fehlgeschlagene Nachricht erneut einplanen (Rolle writer)");
    }

    private static IResult Statistics(SqliteStatisticsReader statistics, string machine, string name,
        DateTime? from, DateTime? to, int? bucketMinutes)
    {
        try
        {
            return Results.Ok(statistics.Query(machine, name, from, to, bucketMinutes));
        }
        catch (InvalidQueryException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (QueryTargetNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
    }
}
