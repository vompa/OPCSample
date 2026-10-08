using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OPCClient.Opc;

namespace OPCClient.Storage;

public record StoredMessage(long Id, OpcMessage Message, int Attempts);

/// <summary>Persistenter Stack (LIFO) für OPC-Nachrichten, ein Datenbestand für alle Maschinen.</summary>
public sealed class SqliteMessageStore
{
    private const string Cols = "id, machine, name, value_json, ts, state, attempts, error";
    private readonly string _cs;

    public SqliteMessageStore(string path)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS messages (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                machine    TEXT NOT NULL,
                name       TEXT NOT NULL,
                value_json TEXT,
                value_type TEXT,
                ts         TEXT NOT NULL,
                state      INTEGER NOT NULL DEFAULT 0,  -- 0 offen, 1 in Arbeit, 2 fertig, 3 fehlgeschlagen
                attempts   INTEGER NOT NULL DEFAULT 0,
                error      TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_pending ON messages(machine, state, id);
            """);

        // Nach Absturz: "in Arbeit" wieder auf "offen" setzen
        Exec(c, "UPDATE messages SET state = 0 WHERE state = 1;");
    }

    /// <summary>
    /// Erlaubte Typen für gespeicherte Werte. Der Typname kommt aus der Datenbank und wird nie blind mit
    /// <c>Type.GetType</c> aufgelöst; alles Unbekannte wird als <see cref="JsonElement"/> geliefert.
    /// </summary>
    private static readonly Dictionary<string, Type> AllowedValueTypes = new[]
        {
            typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
            typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(string), typeof(DateTime), typeof(Guid),
        }
        .SelectMany(t => new[] { t, t.MakeArrayType() })
        .ToDictionary(t => t.FullName!);

    internal static Type? ResolveValueType(string? name) =>
        name is not null && AllowedValueTypes.TryGetValue(name, out var type) ? type : null;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------- Schreiben / Verarbeiten ----------

    public long Push(OpcMessage m)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (machine, name, value_json, value_type, ts)
            VALUES ($m, $n, $v, $t, $ts); SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$m", m.Machine);
        cmd.Parameters.AddWithValue("$n", m.Name);
        cmd.Parameters.AddWithValue("$v", m.Value is null ? DBNull.Value : JsonSerializer.Serialize(m.Value));
        cmd.Parameters.AddWithValue("$t", m.Value?.GetType().FullName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", m.Timestamp.ToUniversalTime().ToString("o"));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Atomar: neueste offene Nachricht holen und auf "in Arbeit" setzen (LIFO).</summary>
    public StoredMessage? TryPop(string machine)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE messages SET state = 1, attempts = attempts + 1
            WHERE id = (SELECT MAX(id) FROM messages WHERE machine = $m AND state = 0)
            RETURNING id, machine, name, value_json, value_type, ts, attempts;
            """;
        cmd.Parameters.AddWithValue("$m", machine);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        object? value = null;
        if (!r.IsDBNull(3) && !r.IsDBNull(4))
        {
            var type = ResolveValueType(r.GetString(4));
            value = type is null
                ? JsonSerializer.Deserialize<JsonElement>(r.GetString(3))
                : JsonSerializer.Deserialize(r.GetString(3), type);
        }

        var msg = new OpcMessage(r.GetString(1), r.GetString(2), value,
            DateTime.Parse(r.GetString(5), null, DateTimeStyles.RoundtripKind), r.GetInt64(0));
        return new StoredMessage(r.GetInt64(0), msg, r.GetInt32(6));
    }

    public void Complete(long id) => Update(id, MessageState.Done, null);

    /// <summary>Gibt eine in Arbeit befindliche Nachricht zurück (z. B. beim Shutdown), ohne den Versuch zu zählen.</summary>
    public void Release(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE messages SET state = 0, attempts = MAX(attempts - 1, 0) WHERE id = $id AND state = 1";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Zurück auf "offen" oder endgültig "fehlgeschlagen" nach maxAttempts.</summary>
    public void Fail(long id, int attempts, string error, int maxAttempts = 3) =>
        Update(id, attempts >= maxAttempts ? MessageState.Failed : MessageState.Pending, error);

    private void Update(long id, MessageState state, string? error)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE messages SET state = $s, error = $e WHERE id = $id";
        cmd.Parameters.AddWithValue("$s", (int)state);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public int PendingCount(string machine)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE machine = $m AND state = 0";
        cmd.Parameters.AddWithValue("$m", machine);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Erledigte Einträge aufräumen.</summary>
    public int Purge(TimeSpan olderThan)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE state = 2 AND ts < $limit";
        cmd.Parameters.AddWithValue("$limit", DateTime.UtcNow.Subtract(olderThan).ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    // ---------- Abfragen für die API ----------

    private static MessageDto Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : JsonSerializer.Deserialize<JsonElement>(r.GetString(3)),
        DateTime.Parse(r.GetString(4), null, DateTimeStyles.RoundtripKind),
        (MessageState)r.GetInt32(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7));

    public PagedResult<MessageDto> Query(MessageFilter f)
    {
        var limit = Math.Clamp(f.Limit, 1, 500);
        var offset = Math.Max(f.Offset, 0);

        var where = new List<string>();
        var ps = new List<(string, object)>();
        if (f.Machine is not null) { where.Add("machine = $machine"); ps.Add(("$machine", f.Machine)); }
        if (f.Name is not null)    { where.Add("name = $name");       ps.Add(("$name", f.Name)); }
        if (f.State is not null)   { where.Add("state = $state");     ps.Add(("$state", (int)f.State)); }
        if (f.From is not null)    { where.Add("ts >= $from");        ps.Add(("$from", f.From.Value.ToUniversalTime().ToString("o"))); }
        if (f.To is not null)      { where.Add("ts <= $to");          ps.Add(("$to", f.To.Value.ToUniversalTime().ToString("o"))); }
        var w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        using var c = Open();

        using var cnt = c.CreateCommand();
        cnt.CommandText = $"SELECT COUNT(*) FROM messages {w}";
        foreach (var (n, v) in ps) cnt.Parameters.AddWithValue(n, v);
        var total = Convert.ToInt32(cnt.ExecuteScalar());

        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM messages {w} ORDER BY id DESC LIMIT $limit OFFSET $offset";
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var items = new List<MessageDto>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(Map(r));
        return new PagedResult<MessageDto>(items, total, limit, offset);
    }

    public MessageDto? Get(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>Aktuellster Wert je Variable einer Maschine.</summary>
    public List<MessageDto> Latest(string machine)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Cols} FROM messages
            WHERE id IN (SELECT MAX(id) FROM messages WHERE machine = $m GROUP BY name)
            ORDER BY name
            """;
        cmd.Parameters.AddWithValue("$m", machine);
        var list = new List<MessageDto>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>Alle Maschinen, zu denen Nachrichten gespeichert sind (auch wenn sie nicht mehr konfiguriert sind).</summary>
    public List<string> MachineNames()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT machine FROM messages ORDER BY machine";
        var names = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    public Dictionary<string, int> Stats(string machine)
    {
        var res = Enum.GetNames<MessageState>().ToDictionary(n => n, _ => 0);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT state, COUNT(*) FROM messages WHERE machine = $m GROUP BY state";
        cmd.Parameters.AddWithValue("$m", machine);
        using var r = cmd.ExecuteReader();
        while (r.Read()) res[((MessageState)r.GetInt32(0)).ToString()] = r.GetInt32(1);
        return res;
    }

    /// <summary>Fehlgeschlagene Nachricht wieder auf "offen"; liefert den Maschinennamen.</summary>
    public string? Requeue(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE messages SET state = 0, attempts = 0, error = NULL
            WHERE id = $id AND state = 3 RETURNING machine
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }
}
