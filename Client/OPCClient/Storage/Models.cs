using System.Text.Json;
using System.Text.Json.Serialization;

namespace OPCClient.Storage;

public enum MessageState { Pending = 0, Processing = 1, Done = 2, Failed = 3 }

public record MessageDto(long Id, string Machine, string Name, JsonElement? Value,
    DateTime Timestamp, MessageState State, int Attempts, string? Error);

public record MessageFilter(string? Machine = null, string? Name = null, MessageState? State = null,
    DateTime? From = null, DateTime? To = null, int Limit = 100, int Offset = 0);

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Limit, int Offset);

public record MachineDto(string Name, bool Connected, Dictionary<string, int> Messages);

[JsonConverter(typeof(JsonStringEnumConverter<AlarmKind>))]
public enum AlarmKind { Low, High }

/// <param name="Limit">Die verletzte Grenze.</param>
/// <param name="Value">Wert, der den Alarm ausgelöst hat.</param>
/// <param name="ClearedValue">Wert, der den Alarm beendet hat (null, solange er aktiv ist).</param>
public record AlarmDto(long Id, string Machine, string Name, AlarmKind Kind, double Limit, double Value,
    DateTime RaisedAt, DateTime? ClearedAt, double? ClearedValue)
{
    public bool Active => ClearedAt is null;
}

/// <param name="From">Auslösezeitpunkt ab.</param>
/// <param name="To">Auslösezeitpunkt bis.</param>
public record AlarmFilter(string? Machine = null, string? Name = null, DateTime? From = null, DateTime? To = null,
    bool ActiveOnly = false, int Limit = 100, int Offset = 0);

public record AuditEntry(DateTime Timestamp, string Client, string Role, string Channel, string Action,
    string? Arguments, string Outcome, string? Error = null, long Id = 0);

public record AuditFilter(string? Client = null, string? Action = null, string? Outcome = null,
    DateTime? From = null, DateTime? To = null, int Limit = 100, int Offset = 0);

/// <summary>Kanäle, über die ein Zugriff kam.</summary>
public static class AuditChannels
{
    public const string Mcp = "mcp";
    public const string Rest = "rest";
}

/// <summary>Ergebnis eines geprüften Zugriffs.</summary>
public static class AuditOutcomes
{
    public const string Ok = "ok";
    public const string Denied = "denied";
    public const string Error = "error";

    public static readonly IReadOnlyList<string> All = [Ok, Denied, Error];
}

public record StatisticsPoint(DateTime Timestamp, double Value);

public record StatisticsBucket(DateTime Start, int Count, double Min, double Max, double Avg);

/// <param name="Note">Hinweis zur Aussagekraft: gespeichert sind nur Wertänderungen.</param>
/// <param name="Buckets">Zeitreihe (nur Buckets mit Werten); null ohne <c>bucketMinutes</c>.</param>
public record StatisticsResult(string Machine, string Name, DateTime From, DateTime To, int Count,
    double? Min, double? Max, double? Avg, StatisticsPoint? First, StatisticsPoint? Last,
    IReadOnlyList<StatisticsBucket>? Buckets, string Note);

/// <summary>Ungültige Abfrage (Zeitraum, Bucket, nicht numerische Variable): der Aufrufer kann sie korrigieren.</summary>
public sealed class InvalidQueryException(string message) : Exception(message);

/// <summary>Maschine oder Variable ist unbekannt.</summary>
public sealed class QueryTargetNotFoundException(string message) : Exception(message);
