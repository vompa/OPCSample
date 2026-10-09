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

    private async Task<OpcMachine> StartAsync(Func<OpcMessage, CancellationToken, Task> handler, TimeSpan? timeout = null,
        Func<int, TimeSpan>? retryDelay = null)
    {
        _machine = new OpcMachine("M1", _store)
        {
            OnMessage = handler,
            HandlerTimeout = timeout ?? TimeSpan.FromSeconds(5),
            RetryDelay = retryDelay ?? (_ => TimeSpan.FromMilliseconds(10)),
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
    public async Task Hanging_handler_is_timed_out_three_times_and_ends_in_failed_without_a_fourth_attempt()
    {
        var id = Push(1);
        var calls = 0;
        var retryDelays = new List<int>();

        await StartAsync(
            (_, _) => { Interlocked.Increment(ref calls); return Task.Delay(Timeout.Infinite); },
            timeout: TimeSpan.FromMilliseconds(50),
            retryDelay: failedAttempt => { lock (retryDelays) retryDelays.Add(failedAttempt); return TimeSpan.FromMilliseconds(1); });
        await WaitUntilAsync(() => _store.Get(id)!.State == MessageState.Failed, "Failed durch dreifachen Timeout");

        var stored = _store.Get(id)!;
        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Equal(3, stored.Attempts);
        Assert.Equal("Timeout", stored.Error);
        // Kein vierter Versuch: Nach dem dritten Fehlschlag wird kein weiterer Retry eingeplant (nur nach Versuch 1 und 2)
        lock (retryDelays) Assert.Equal([1, 2], retryDelays);
    }

    [Fact]
    public async Task Throwing_handler_is_tried_three_times_and_ends_in_failed_without_a_fourth_attempt()
    {
        var id = Push(1);
        var calls = 0;
        var retryDelays = new List<int>();

        await StartAsync(
            (_, _) => { Interlocked.Increment(ref calls); throw new InvalidOperationException("kaputt"); },
            retryDelay: failedAttempt => { lock (retryDelays) retryDelays.Add(failedAttempt); return TimeSpan.FromMilliseconds(1); });
        await WaitUntilAsync(() => _store.Get(id)!.State == MessageState.Failed, "Failed durch dreifache Exception");

        var stored = _store.Get(id)!;
        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Equal(3, stored.Attempts);
        Assert.Equal("kaputt", stored.Error);
        lock (retryDelays) Assert.Equal([1, 2], retryDelays);
    }

    [Fact]
    public async Task Requeued_failed_message_is_processed_again_after_wake()
    {
        var id = Push(1);
        var calls = 0;
        var fail = true;

        await StartAsync((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Volatile.Read(ref fail) ? throw new InvalidOperationException("kaputt") : Task.CompletedTask;
        });
        await WaitUntilAsync(() => _store.Get(id)!.State == MessageState.Failed, "Failed");
        Assert.Equal(3, Volatile.Read(ref calls));

        Volatile.Write(ref fail, false);
        Assert.Equal("M1", _store.Requeue(id));
        _machine!.Wake();
        await WaitUntilAsync(() => _store.Get(id)!.State == MessageState.Done, "erneute Verarbeitung nach Wake");

        var stored = _store.Get(id)!;
        Assert.Equal(4, Volatile.Read(ref calls));
        Assert.Equal(1, stored.Attempts);
        Assert.Null(stored.Error);
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
