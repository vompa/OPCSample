using Microsoft.Extensions.Options;
using Serilog;

namespace OPCClient.Storage;

/// <summary>Konfiguration unter <c>Retention</c>.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Erledigte Nachrichten, die älter sind, werden gelöscht. Fehlgeschlagene bleiben für das Retry erhalten.</summary>
    public int DoneRetentionDays { get; set; } = 7;

    /// <summary>Beendete Alarme, die älter sind, werden gelöscht. Aktive Alarme bleiben immer erhalten.</summary>
    public int AlarmRetentionDays { get; set; } = 30;

    /// <summary>Audit-Einträge, die älter sind, werden gelöscht.</summary>
    public int AuditRetentionDays { get; set; } = 90;

    public int IntervalMinutes { get; set; } = 60;
}

/// <summary>Anzahl der entfernten Einträge je Tabelle.</summary>
public record RetentionResult(int Messages, int Alarms, int AuditEntries);

/// <summary>Räumt regelmäßig erledigte Nachrichten, beendete Alarme und alte Audit-Einträge auf, damit die Datenbank nicht unbegrenzt wächst.</summary>
public sealed class RetentionService(
    SqliteMessageStore messages, SqliteAlarmStore alarms, SqliteAuditStore audit, IOptions<RetentionOptions> options)
    : BackgroundService
{
    public RetentionResult RunOnce()
    {
        var o = options.Value;
        var result = new RetentionResult(
            messages.Purge(Days(o.DoneRetentionDays)),
            alarms.PurgeCleared(Days(o.AlarmRetentionDays)),
            audit.Purge(Days(o.AuditRetentionDays)));

        if (result != new RetentionResult(0, 0, 0))
            Log.Information("Aufräumen: {Messages} erledigte Nachrichten, {Alarms} beendete Alarme, {Audit} Audit-Einträge entfernt",
                result.Messages, result.Alarms, result.AuditEntries);
        return result;
    }

    /// <summary>Mindestens ein Tag, damit eine falsche Konfiguration (0, negativ) nicht alles löscht.</summary>
    private static TimeSpan Days(int days) => TimeSpan.FromDays(Math.Max(days, 1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(options.Value.IntervalMinutes, 1)));
        do
        {
            try { RunOnce(); }
            catch (Exception ex) { Log.Error(ex, "Aufräumen fehlgeschlagen"); }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
