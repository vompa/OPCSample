using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OPCClient.Storage;

/// <summary>Gemeinsamer Zugang zur SQLite-Datei: alle Stores (Nachrichten, Alarme, Audit) teilen sich eine Datenbank.</summary>
internal sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000;");
        return c;
    }

    /// <summary>Legt Tabellen und Indizes an (idempotent) und schaltet den WAL-Modus ein.</summary>
    public void EnsureSchema(string sql)
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, sql);
    }

    public static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Zeitstempel als UTC in ISO 8601 ("o"); sortiert als Text korrekt.</summary>
    public static string FormatTimestamp(DateTime value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static DateTime ParseTimestamp(string value) => DateTime.Parse(value, null, DateTimeStyles.RoundtripKind);

    /// <summary>Wendet die Teilbedingungen und Parameter eines Filters an und liefert die WHERE-Klausel (leer ohne Bedingung).</summary>
    public static string BuildWhere(IReadOnlyCollection<string> conditions) =>
        conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
}
