using Microsoft.Data.Sqlite;

namespace OPCClient.Storage;

/// <summary>
/// Statistik über gespeicherte numerische Wertänderungen, berechnet in SQLite.
/// Es werden nur Änderungen gespeichert (DistinctUntilChanged): Min, Max und Mittel beziehen sich auf die Änderungen,
/// nicht auf die Zeit. Ein Wert, der lange konstant war, zählt nur einmal.
/// </summary>
public sealed class SqliteStatisticsReader
{
    public const int MaxRangeDays = 31;
    public const int MaxBuckets = 200;
    public const int MinBucketMinutes = 1;
    public const int MaxBucketMinutes = 1440;
    public const string Note = "Statistik über gespeicherte Wertänderungen, nicht zeitgewichtet: Es werden nur Änderungen " +
                               "gespeichert, ein lange konstanter Wert zählt nur einmal.";

    private static readonly TimeSpan DefaultRange = TimeSpan.FromHours(1);

    private static readonly string[] NumericTypes = new[]
        {
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
            typeof(long), typeof(ulong), typeof(float), typeof(double),
        }
        .Select(t => t.FullName!).ToArray();

    // Aus festen Typnamen (keine Nutzereingabe) zusammengesetzt
    private static readonly string NumericTypeList = string.Join(", ", NumericTypes.Select(t => $"'{t}'"));

    private readonly SqliteDatabase _db;

    public SqliteStatisticsReader(string path) => _db = new SqliteDatabase(path);

    /// <param name="to">Standard: jetzt.</param>
    /// <param name="from">Standard: eine Stunde vor <paramref name="to"/>.</param>
    /// <exception cref="InvalidQueryException">Zeitraum oder Bucket ungültig, Variable nicht numerisch.</exception>
    /// <exception cref="QueryTargetNotFoundException">Für Maschine und Variable sind keine Werte gespeichert.</exception>
    public StatisticsResult Query(string machine, string name, DateTime? from, DateTime? to, int? bucketMinutes)
    {
        var end = (to ?? DateTime.UtcNow).ToUniversalTime();
        var start = (from ?? end - DefaultRange).ToUniversalTime();
        ValidateRange(start, end);
        var bucketSpan = ValidateBuckets(start, end, bucketMinutes);

        using var c = _db.Open();
        EnsureNumeric(c, machine, name);

        var scope = new Scope(machine, name, SqliteDatabase.FormatTimestamp(start), SqliteDatabase.FormatTimestamp(end));
        var (count, min, max, avg) = ReadSummary(c, scope);
        return new StatisticsResult(machine, name, start, end, count, min, max, avg,
            count > 0 ? ReadEdge(c, scope, "ASC") : null,
            count > 0 ? ReadEdge(c, scope, "DESC") : null,
            bucketSpan is { } span ? ReadBuckets(c, scope, start, span) : null,
            Note);
    }

    private sealed record Scope(string Machine, string Name, string From, string To);

    private const string SeriesFilter = """
        FROM messages
        WHERE machine = $m AND name = $n AND ts >= $from AND ts <= $to AND value_json IS NOT NULL
        """;

    private static SqliteCommand Command(SqliteConnection c, string sql, Scope s)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql.Replace("{series}", SeriesFilter, StringComparison.Ordinal)
                             .Replace("{numeric}", $"value_type IN ({NumericTypeList})", StringComparison.Ordinal);
        cmd.Parameters.AddWithValue("$m", s.Machine);
        cmd.Parameters.AddWithValue("$n", s.Name);
        cmd.Parameters.AddWithValue("$from", s.From);
        cmd.Parameters.AddWithValue("$to", s.To);
        return cmd;
    }

    private static void ValidateRange(DateTime start, DateTime end)
    {
        if (start >= end)
            throw new InvalidQueryException("Der Zeitraum ist leer: 'from' muss vor 'to' liegen.");
        if (end - start > TimeSpan.FromDays(MaxRangeDays))
            throw new InvalidQueryException($"Der Zeitraum ist zu groß: höchstens {MaxRangeDays} Tage. Wähle ein kleineres Intervall mit 'from' und 'to'.");
    }

    /// <summary>Prüft die Bucket-Größe und liefert sie als Zeitspanne (null = keine Zeitreihe).</summary>
    private static TimeSpan? ValidateBuckets(DateTime start, DateTime end, int? bucketMinutes)
    {
        if (bucketMinutes is null) return null;
        if (bucketMinutes is < MinBucketMinutes or > MaxBucketMinutes)
            throw new InvalidQueryException($"'bucketMinutes' muss zwischen {MinBucketMinutes} und {MaxBucketMinutes} liegen.");

        var span = TimeSpan.FromMinutes(bucketMinutes.Value);
        var buckets = Math.Ceiling((end - start) / span);
        if (buckets > MaxBuckets)
            throw new InvalidQueryException($"Der Zeitraum ergäbe {buckets} Buckets, erlaubt sind höchstens {MaxBuckets}. " +
                                            "Vergrößere 'bucketMinutes' oder verkürze den Zeitraum.");
        return span;
    }

    /// <summary>Unbekannte Variable bzw. nicht numerischer Typ melden, bevor gerechnet wird (unabhängig vom Zeitraum).</summary>
    private static void EnsureNumeric(SqliteConnection c, string machine, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT value_type FROM messages WHERE machine = $m AND name = $n AND value_json IS NOT NULL";
        cmd.Parameters.AddWithValue("$m", machine);
        cmd.Parameters.AddWithValue("$n", name);

        var types = new List<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) types.Add(r.IsDBNull(0) ? "unbekannt" : r.GetString(0));

        if (types.Count == 0)
            throw new QueryTargetNotFoundException(
                $"Für Maschine '{machine}' und Variable '{name}' sind keine Werte gespeichert. Prüfe die Namen mit list_machines und get_latest_values.");
        if (!types.Any(NumericTypes.Contains))
            throw new InvalidQueryException(
                $"Die Variable '{name}' ist nicht numerisch (Typ {string.Join(", ", types.Select(ShortTypeName))}); Statistik ist nur für Zahlen möglich.");
    }

    private static string ShortTypeName(string fullName) => fullName.Replace("System.", "", StringComparison.Ordinal);

    private static (int Count, double? Min, double? Max, double? Avg) ReadSummary(SqliteConnection c, Scope s)
    {
        using var cmd = Command(c, "SELECT COUNT(*), MIN(CAST(value_json AS REAL)), MAX(CAST(value_json AS REAL)), AVG(CAST(value_json AS REAL)) {series} AND {numeric}", s);
        using var r = cmd.ExecuteReader();
        r.Read();
        var count = r.GetInt32(0);
        return count == 0 ? (0, null, null, null) : (count, r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
    }

    /// <summary>Erster (ASC) bzw. letzter (DESC) Wert im Zeitraum. <paramref name="direction"/> ist eine feste Konstante.</summary>
    private static StatisticsPoint? ReadEdge(SqliteConnection c, Scope s, string direction)
    {
        using var cmd = Command(c, $"SELECT ts, CAST(value_json AS REAL) {{series}} AND {{numeric}} ORDER BY ts {direction}, id {direction} LIMIT 1", s);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new StatisticsPoint(SqliteDatabase.ParseTimestamp(r.GetString(0)), r.GetDouble(1)) : null;
    }

    private static List<StatisticsBucket> ReadBuckets(SqliteConnection c, Scope s, DateTime start, TimeSpan span)
    {
        using var cmd = Command(c, """
            SELECT (CAST(strftime('%s', ts) AS INTEGER) - $startEpoch) / $spanSeconds AS bucket,
                   COUNT(*), MIN(CAST(value_json AS REAL)), MAX(CAST(value_json AS REAL)), AVG(CAST(value_json AS REAL))
            {series} AND {numeric}
            GROUP BY bucket ORDER BY bucket
            """, s);
        cmd.Parameters.AddWithValue("$startEpoch", new DateTimeOffset(start).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$spanSeconds", (long)span.TotalSeconds);

        var buckets = new List<StatisticsBucket>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            buckets.Add(new StatisticsBucket(start + r.GetInt64(0) * span, r.GetInt32(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4)));
        return buckets;
    }
}
