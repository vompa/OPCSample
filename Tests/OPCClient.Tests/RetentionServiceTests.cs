using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

public sealed class RetentionServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"opc-retention-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(file);
    }

    [Fact]
    public void RunOnce_removes_old_done_messages_but_keeps_failed_and_pending()
    {
        var store = new SqliteMessageStore(_path);
        var old = DateTime.UtcNow.AddDays(-30);
        var oldDone = store.Push(new OpcMessage("M1", "T", 1, old));
        var oldFailed = store.Push(new OpcMessage("M1", "T", 2, old));
        var oldPending = store.Push(new OpcMessage("M1", "T", 3, old));
        var freshDone = store.Push(new OpcMessage("M1", "T", 4, DateTime.UtcNow));
        store.Complete(oldDone);
        store.Complete(freshDone);
        store.Fail(oldFailed, attempts: 3, error: "x", maxAttempts: 3);
        var service = new RetentionService(store, Options.Create(new RetentionOptions { DoneRetentionDays = 7 }));

        var removed = service.RunOnce();

        Assert.Equal(1, removed);
        Assert.Null(store.Get(oldDone));
        Assert.NotNull(store.Get(oldFailed));
        Assert.NotNull(store.Get(oldPending));
        Assert.NotNull(store.Get(freshDone));
    }

    [Fact]
    public void RunOnce_with_invalid_retention_falls_back_to_at_least_one_day()
    {
        var store = new SqliteMessageStore(_path);
        var id = store.Push(new OpcMessage("M1", "T", 1, DateTime.UtcNow));
        store.Complete(id);
        var service = new RetentionService(store, Options.Create(new RetentionOptions { DoneRetentionDays = 0 }));

        Assert.Equal(0, service.RunOnce());
        Assert.NotNull(store.Get(id));
    }
}
