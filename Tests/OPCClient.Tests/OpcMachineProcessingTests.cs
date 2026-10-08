using Microsoft.Data.Sqlite;
using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

/// <summary>
/// Testet Worker, LIFO, Retry, Timeout und Shutdown von <see cref="OpcMachine"/> ohne OPC-Server:
/// Die Verbindung schlägt absichtlich fehl, die Verarbeitung der Datenbank-Nachrichten läuft trotzdem
/// (genau das ist die Eigenschaft "OPC-Server optional").
/// </summary>
public sealed class OpcMachineProcessingTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"opc-machine-{Guid.NewGuid():N}.db");
    private SqliteMessageStore _store = null!;
    private OpcMachine? _machine;

    public Task InitializeAsync()
    {
        _store = new SqliteMessageStore(_path);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_machine is not null) await _machine.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(file);
    }

    private long Push(object? value) => _store.Push(new OpcMessage("M1", "T", value, DateTime.UtcNow));

    private async Task<OpcMachine> StartAsync(Func<OpcMessage, CancellationToken, Task> handler, TimeSpan? timeout = null)
    {
        _machine = new OpcMachine("M1", _store)
        {
            OnMessage = handler,
            HandlerTimeout = timeout ?? TimeSpan.FromSeconds(5),
            RetryDelay = _ => TimeSpan.FromMilliseconds(10),
            ConnectRetryInterval = TimeSpan.FromHours(1),
        };
        await _machine.StartAsync(_ => throw new InvalidOperationException("kein OPC-Server in diesem Test"));
        return _machine;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Zeitüberschreitung beim Warten auf: {what}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Newest_message_is_processed_first_even_without_opc_connection()
    {
        Push(1); Push(2); Push(3);
        var seen = new List<object?>();

        await StartAsync((m, _) => { lock (seen) seen.Add(m.Value); return Task.CompletedTask; });
        await WaitUntilAsync(() => _store.Stats("M1")["Done"] == 3, "3 erledigte Nachrichten");

        Assert.Equal([3, 2, 1], seen.Cast<int>());
        Assert.False(_machine!.IsConnected);
    }

    [Fact]
    public async Task Handler_receives_the_database_id_as_idempotency_basis()
    {
        var id = Push(1);
        long? receivedId = null;

        await StartAsync((m, _) => { receivedId = m.Id; return Task.CompletedTask; });
        await WaitUntilAsync(() => receivedId is not null, "Handleraufruf");

        Assert.Equal(id, receivedId);
    }

    [Fact]
    public async Task Failing_handler_is_retried_and_eventually_succeeds()
    {
        var id = Push(1);
        var calls = 0;

        await StartAsync((_, _) => ++calls < 3 ? throw new InvalidOperationException("noch nicht") : Task.CompletedTask);
        await WaitUntilAsync(() => _store.Get(id)!.State == MessageState.Done, "erfolgreicher dritter Versuch");

        Assert.Equal(3, calls);
        Assert.Equal(3, _store.Get(id)!.Attempts);
    }

    [Fact]
    public async Task Always_failing_handler_ends_in_failed_after_three_attempts()
    {
        var id = Push(1);
        var calls = 0;

        await StartAsync((_, _) => { Interlocked.Increment(ref calls); throw new InvalidOperationException("kaputt"); });
        await WaitUntilAsync(() => _store.Get(id)!.State == MessageState.Failed, "Failed");
        await Task.Delay(100); // es darf kein vierter Versuch folgen

        Assert.Equal(3, calls);
        Assert.Equal("kaputt", _store.Get(id)!.Error);
    }

    [Fact]
    public async Task Handler_that_ignores_cancellation_is_cut_off_by_the_timeout()
    {
        var id = Push(1);

        await StartAsync((_, _) => Task.Delay(Timeout.Infinite), timeout: TimeSpan.FromMilliseconds(50));
        await WaitUntilAsync(() => _store.Get(id)!.State == MessageState.Failed, "Failed durch Timeout");

        Assert.Equal("Timeout", _store.Get(id)!.Error);
    }

    [Fact]
    public async Task Shutdown_during_processing_keeps_the_message_pending_without_using_an_attempt()
    {
        // Regression: früher verbrauchte jedes Herunterfahren während der Verarbeitung einen Versuch
        var id = Push(1);
        var started = new TaskCompletionSource();

        await StartAsync(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _machine!.DisposeAsync();

        var stored = _store.Get(id)!;
        Assert.Equal(MessageState.Pending, stored.State);
        Assert.Equal(0, stored.Attempts);
    }
}
