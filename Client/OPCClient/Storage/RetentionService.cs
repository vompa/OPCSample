using Microsoft.Extensions.Options;
using Serilog;

namespace OPCClient.Storage;

/// <summary>Konfiguration unter <c>Retention</c>.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Erledigte Nachrichten, die älter sind, werden gelöscht. Fehlgeschlagene bleiben für das Retry erhalten.</summary>
    public int DoneRetentionDays { get; set; } = 7;

    public int IntervalMinutes { get; set; } = 60;
}

/// <summary>Räumt regelmäßig erledigte Nachrichten auf, damit die Datenbank nicht unbegrenzt wächst.</summary>
public sealed class RetentionService(SqliteMessageStore store, IOptions<RetentionOptions> options) : BackgroundService
{
    public int RunOnce()
    {
        var days = Math.Max(options.Value.DoneRetentionDays, 1);
        var removed = store.Purge(TimeSpan.FromDays(days));
        if (removed > 0)
            Log.Information("Aufräumen: {Removed} erledigte Nachrichten älter als {Days} Tage entfernt", removed, days);
        return removed;
    }

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
