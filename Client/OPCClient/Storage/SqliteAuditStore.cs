using Microsoft.Data.Sqlite;

namespace OPCClient.Storage;

/// <summary>Persistentes Audit-Log: wer hat wann über welchen Kanal welche Aktion ausgeführt (nur anhängen, lesen, aufräumen).</summary>
public sealed class SqliteAuditStore
{
    private const string Cols = "id, ts, client, role, channel, action, arguments, outcome, error";
    private const int MaxLimit = 500;
    private readonly SqliteDatabase _db;

    public SqliteAuditStore(string path)
    {
        _db = new SqliteDatabase(path);
        _db.EnsureSchema("""
            CREATE TABLE IF NOT EXISTS audit (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                ts        TEXT NOT NULL,
                client    TEXT NOT NULL,
                role      TEXT NOT NULL,
                channel   TEXT NOT NULL,   -- mcp oder rest
                action    TEXT NOT NULL,
                arguments TEXT,
                outcome   TEXT NOT NULL,   -- ok, denied oder error
                error     TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_audit_ts ON audit(ts);
            """);
    }

    public void Add(AuditEntry e)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit (ts, client, role, channel, action, arguments, outcome, error)
            VALUES ($ts, $client, $role, $channel, $action, $arguments, $outcome, $error)
            """;
        cmd.Parameters.AddWithValue("$ts", SqliteDatabase.FormatTimestamp(e.Timestamp));
        cmd.Parameters.AddWithValue("$client", e.Client);
        cmd.Parameters.AddWithValue("$role", e.Role);
        cmd.Parameters.AddWithValue("$channel", e.Channel);
        cmd.Parameters.AddWithValue("$action", e.Action);
        cmd.Parameters.AddWithValue("$arguments", (object?)e.Arguments ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", e.Outcome);
        cmd.Parameters.AddWithValue("$error", (object?)e.Error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Neueste zuerst.</summary>
    public PagedResult<AuditEntry> Query(AuditFilter f)
    {
        var limit = Math.Clamp(f.Limit, 1, MaxLimit);
        var offset = Math.Max(f.Offset, 0);

        var where = new List<string>();
        var ps = new List<(string, object)>();
        if (f.Client is not null)  { where.Add("client = $client");   ps.Add(("$client", f.Client)); }
        if (f.Action is not null)  { where.Add("action = $action");   ps.Add(("$action", f.Action)); }
        if (f.Outcome is not null) { where.Add("outcome = $outcome"); ps.Add(("$outcome", f.Outcome)); }
        if (f.From is not null)    { where.Add("ts >= $from");        ps.Add(("$from", SqliteDatabase.FormatTimestamp(f.From.Value))); }
        if (f.To is not null)      { where.Add("ts <= $to");          ps.Add(("$to", SqliteDatabase.FormatTimestamp(f.To.Value))); }
        var w = SqliteDatabase.BuildWhere(where);

        using var c = _db.Open();

        using var cnt = c.CreateCommand();
        cnt.CommandText = $"SELECT COUNT(*) FROM audit {w}";
        foreach (var (n, v) in ps) cnt.Parameters.AddWithValue(n, v);
        var total = Convert.ToInt32(cnt.ExecuteScalar());

        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM audit {w} ORDER BY id DESC LIMIT $limit OFFSET $offset";
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var items = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(Map(r));
        return new PagedResult<AuditEntry>(items, total, limit, offset);
    }

    public int Purge(TimeSpan olderThan)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM audit WHERE ts < $limit";
        cmd.Parameters.AddWithValue("$limit", SqliteDatabase.FormatTimestamp(DateTime.UtcNow.Subtract(olderThan)));
        return cmd.ExecuteNonQuery();
    }

    private static AuditEntry Map(SqliteDataReader r) => new(
        SqliteDatabase.ParseTimestamp(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetInt64(0));
}
