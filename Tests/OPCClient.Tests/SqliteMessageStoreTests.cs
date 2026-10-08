using System.Text.Json;
using Microsoft.Data.Sqlite;
using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

public sealed class SqliteMessageStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"opc-test-{Guid.NewGuid():N}.db");
    private readonly SqliteMessageStore _store;

    public SqliteMessageStoreTests() => _store = new SqliteMessageStore(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(file);
    }

    private static OpcMessage Msg(string name, object? value, DateTime? timestamp = null, string machine = "M1") =>
        new(machine, name, value, timestamp ?? DateTime.UtcNow);

    [Fact]
    public void Pop_returns_newest_message_first()
    {
        _store.Push(Msg("T", 1));
        _store.Push(Msg("T", 2));
        var newest = _store.Push(Msg("T", 3));

        var popped = _store.TryPop("M1");

        Assert.NotNull(popped);
        Assert.Equal(newest, popped.Id);
        Assert.Equal(3, popped.Message.Value);
        Assert.Equal(1, popped.Attempts);
    }

    [Fact]
    public void Pop_returns_null_when_nothing_is_pending_and_respects_machine()
    {
        _store.Push(Msg("T", 1, machine: "Other"));

        Assert.Null(_store.TryPop("M1"));
    }

    [Fact]
    public void Failed_message_returns_to_pending_until_max_attempts_then_fails()
    {
        var id = _store.Push(Msg("T", 1));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var popped = _store.TryPop("M1");
            Assert.Equal(id, popped!.Id);
            Assert.Equal(attempt, popped.Attempts);
            _store.Fail(id, popped.Attempts, "boom", maxAttempts: 3);
        }

        Assert.Null(_store.TryPop("M1"));
        Assert.Equal(MessageState.Failed, _store.Get(id)!.State);
        Assert.Equal("boom", _store.Get(id)!.Error);
    }

    [Fact]
    public void Release_returns_message_without_counting_the_attempt()
    {
        // Regression: ein Shutdown während der Verarbeitung darf keinen Versuch verbrauchen
        var id = _store.Push(Msg("T", 1));
        _store.TryPop("M1");

        _store.Release(id);

        var again = _store.TryPop("M1");
        Assert.Equal(1, again!.Attempts);
    }

    [Fact]
    public void Release_does_nothing_for_messages_that_are_not_in_progress()
    {
        var id = _store.Push(Msg("T", 1));

        _store.Release(id);

        Assert.Equal(MessageState.Pending, _store.Get(id)!.State);
        Assert.Equal(0, _store.Get(id)!.Attempts);
    }

    [Fact]
    public void Requeue_only_works_for_failed_messages()
    {
        var pending = _store.Push(Msg("T", 1));
        var failed = _store.Push(Msg("T", 2));
        _store.TryPop("M1");
        _store.Fail(failed, 3, "x", maxAttempts: 3);

        Assert.Null(_store.Requeue(pending));
        Assert.Equal("M1", _store.Requeue(failed));
        Assert.Equal(MessageState.Pending, _store.Get(failed)!.State);
        Assert.Equal(0, _store.Get(failed)!.Attempts);
    }

    [Fact]
    public void Messages_in_progress_are_restored_after_restart()
    {
        _store.Push(Msg("T", 1));
        _store.TryPop("M1");
        Assert.Equal(0, _store.PendingCount("M1"));

        var restarted = new SqliteMessageStore(_path);

        Assert.Equal(1, restarted.PendingCount("M1"));
    }

    [Fact]
    public void Purge_removes_only_old_done_messages()
    {
        var oldDone = _store.Push(Msg("T", 1, DateTime.UtcNow.AddDays(-10)));
        var freshDone = _store.Push(Msg("T", 2));
        var oldPending = _store.Push(Msg("T", 3, DateTime.UtcNow.AddDays(-10)));
        _store.Complete(oldDone);
        _store.Complete(freshDone);

        var removed = _store.Purge(TimeSpan.FromDays(7));

        Assert.Equal(1, removed);
        Assert.Null(_store.Get(oldDone));
        Assert.NotNull(_store.Get(freshDone));
        Assert.NotNull(_store.Get(oldPending));
    }

    [Fact]
    public void Query_filters_pages_and_caps_the_limit()
    {
        for (var i = 0; i < 5; i++) _store.Push(Msg("A", i));
        _store.Push(Msg("B", 99));

        var onlyA = _store.Query(new MessageFilter(Name: "A", Limit: 2, Offset: 1));
        var capped = _store.Query(new MessageFilter(Limit: 100_000));

        Assert.Equal(5, onlyA.Total);
        Assert.Equal(2, onlyA.Items.Count);
        Assert.Equal(500, capped.Limit);
        Assert.Equal("B", capped.Items[0].Name); // neueste zuerst
    }

    [Fact]
    public void Latest_returns_newest_value_per_variable()
    {
        _store.Push(Msg("Temperatur", 20));
        _store.Push(Msg("Status", "Run"));
        _store.Push(Msg("Temperatur", 25));

        var latest = _store.Latest("M1");

        Assert.Equal(["Status", "Temperatur"], latest.Select(m => m.Name));
        Assert.Equal(25, latest.Single(m => m.Name == "Temperatur").Value!.Value.GetInt32());
    }

    [Fact]
    public void Stats_counts_every_state()
    {
        var done = _store.Push(Msg("T", 1));
        _store.Push(Msg("T", 2));
        _store.Complete(done);

        var stats = _store.Stats("M1");

        Assert.Equal(1, stats["Done"]);
        Assert.Equal(1, stats["Pending"]);
        Assert.Equal(0, stats["Failed"]);
    }

    [Fact]
    public void Values_survive_a_round_trip_with_their_type()
    {
        _store.Push(Msg("Int", 42));
        _store.Push(Msg("Array", new[] { 1.5, 2.5 }));
        _store.Push(Msg("Nothing", null));

        var nothing = _store.TryPop("M1")!.Message.Value;
        var array = _store.TryPop("M1")!.Message.Value;
        var number = _store.TryPop("M1")!.Message.Value;

        Assert.Null(nothing);
        Assert.Equal(new[] { 1.5, 2.5 }, Assert.IsType<double[]>(array));
        Assert.Equal(42, Assert.IsType<int>(number));
    }

    [Fact]
    public void Unknown_stored_type_is_not_instantiated_but_delivered_as_json()
    {
        using (var c = new SqliteConnection($"Data Source={_path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages (machine, name, value_json, value_type, ts)
                VALUES ('M1', 'X', '{"a":1}', 'System.Diagnostics.Process', '2026-01-01T00:00:00.0000000Z')
                """;
            cmd.ExecuteNonQuery();
        }

        var popped = _store.TryPop("M1");

        var json = Assert.IsType<JsonElement>(popped!.Message.Value);
        Assert.Equal(1, json.GetProperty("a").GetInt32());
    }

    [Theory]
    [InlineData("System.Int32", typeof(int))]
    [InlineData("System.Double[]", typeof(double[]))]
    [InlineData("System.String", typeof(string))]
    public void Allowed_value_types_resolve(string name, Type expected) =>
        Assert.Equal(expected, SqliteMessageStore.ResolveValueType(name));

    [Theory]
    [InlineData("System.Diagnostics.Process")]
    [InlineData("System.IO.FileInfo")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_value_types_are_not_resolved(string? name) =>
        Assert.Null(SqliteMessageStore.ResolveValueType(name));
}
