using Microsoft.Data.Sqlite;

namespace OPCClient.Storage;

/// <summary>
/// Alarme in SQLite. Pro Maschine, Variable und Art gibt es höchstens einen aktiven Alarm; das erzwingt ein
/// Teil-Index in der Datenbank, nicht nur der Code. Der Zustand überlebt damit jeden Neustart.
/// </summary>
public sealed class SqliteAlarmStore
{
    private const string Cols = "id, machine, name, kind, limit_value, value, raised_at, cleared_at, cleared_value";
    private const int MaxLimit = 500;
    private readonly SqliteDatabase _db;

    public SqliteAlarmStore(string path)
    {
        _db = new SqliteDatabase(path);
        _db.EnsureSchema("""
            CREATE TABLE IF NOT EXISTS alarms (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                machine       TEXT NOT NULL,
                name          TEXT NOT NULL,
                kind          TEXT NOT NULL,           -- High oder Low
                limit_value   REAL NOT NULL,           -- verletzte Grenze
                value         REAL NOT NULL,           -- auslösender Wert
                raised_at     TEXT NOT NULL,
                cleared_at    TEXT,                    -- NULL = aktiv
                cleared_value REAL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_alarm_active ON alarms(machine, name, kind) WHERE cleared_at IS NULL;
            CREATE INDEX IF NOT EXISTS ix_alarm_time ON alarms(raised_at);
            """);
    }

    /// <summary>Löst den Alarm aus, falls für diese Art noch keiner aktiv ist. Liefert true, wenn ein neuer entstanden ist.</summary>
    public bool Raise(string machine, string name, AlarmKind kind, double limit, double value, DateTime timestamp)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO alarms (machine, name, kind, limit_value, value, raised_at)
            VALUES ($m, $n, $k, $l, $v, $ts)
            """;
        cmd.Parameters.AddWithValue("$m", machine);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$k", kind.ToString());
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$ts", SqliteDatabase.FormatTimestamp(timestamp));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Beendet den aktiven Alarm dieser Art. Liefert true, wenn einer aktiv war.</summary>
    public bool Clear(string machine, string name, AlarmKind kind, double value, DateTime timestamp)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE alarms SET cleared_at = $ts, cleared_value = $v
            WHERE machine = $m AND name = $n AND kind = $k AND cleared_at IS NULL
            """;
        cmd.Parameters.AddWithValue("$m", machine);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$k", kind.ToString());
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$ts", SqliteDatabase.FormatTimestamp(timestamp));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Alle aktiven Alarme, optional einer Maschine. Die Zahl ist durch Variablen mal zwei Arten begrenzt.</summary>
    public IReadOnlyList<AlarmDto> Active(string? machine = null) =>
        Query(new AlarmFilter(machine, ActiveOnly: true, Limit: MaxLimit)).Items;

    public PagedResult<AlarmDto> Query(AlarmFilter f)
    {
        var limit = Math.Clamp(f.Limit, 1, MaxLimit);
        var offset = Math.Max(f.Offset, 0);

        var where = new List<string>();
        var ps = new List<(string, object)>();
        if (f.Machine is not null) { where.Add("machine = $machine"); ps.Add(("$machine", f.Machine)); }
        if (f.Name is not null)    { where.Add("name = $name");       ps.Add(("$name", f.Name)); }
        if (f.From is not null)    { where.Add("raised_at >= $from"); ps.Add(("$from", SqliteDatabase.FormatTimestamp(f.From.Value))); }
        if (f.To is not null)      { where.Add("raised_at <= $to");   ps.Add(("$to", SqliteDatabase.FormatTimestamp(f.To.Value))); }
        if (f.ActiveOnly)          { where.Add("cleared_at IS NULL"); }
        var w = SqliteDatabase.BuildWhere(where);

        using var c = _db.Open();

        using var cnt = c.CreateCommand();
        cnt.CommandText = $"SELECT COUNT(*) FROM alarms {w}";
        foreach (var (n, v) in ps) cnt.Parameters.AddWithValue(n, v);
        var total = Convert.ToInt32(cnt.ExecuteScalar());

        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM alarms {w} ORDER BY id DESC LIMIT $limit OFFSET $offset";
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var items = new List<AlarmDto>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(Map(r));
        return new PagedResult<AlarmDto>(items, total, limit, offset);
    }

    /// <summary>Beendete Alarme aufräumen; aktive bleiben immer erhalten.</summary>
    public int PurgeCleared(TimeSpan olderThan)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM alarms WHERE cleared_at IS NOT NULL AND cleared_at < $limit";
        cmd.Parameters.AddWithValue("$limit", SqliteDatabase.FormatTimestamp(DateTime.UtcNow.Subtract(olderThan)));
        return cmd.ExecuteNonQuery();
    }

    private static AlarmDto Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), Enum.Parse<AlarmKind>(r.GetString(3)),
        r.GetDouble(4), r.GetDouble(5), SqliteDatabase.ParseTimestamp(r.GetString(6)),
        r.IsDBNull(7) ? null : SqliteDatabase.ParseTimestamp(r.GetString(7)),
        r.IsDBNull(8) ? null : r.GetDouble(8));
}
