using Microsoft.Data.Sqlite;

namespace OPCClient.Tests;

/// <summary>Eigene SQLite-Datei je Test, wird danach samt WAL/SHM gelöscht.</summary>
public sealed class TempDatabase : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"opc-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
            File.Delete(file);
    }
}
