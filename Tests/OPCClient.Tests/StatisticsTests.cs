using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

public sealed class StatisticsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempDatabase _db = new();
    private readonly SqliteMessageStore _messages;
    private readonly SqliteStatisticsReader _reader;

    public StatisticsTests()
    {
        _messages = new SqliteMessageStore(_db.Path);
        _reader = new SqliteStatisticsReader(_db.Path);
    }

    public void Dispose() => _db.Dispose();

    private void Add(double value, int minute, string name = "Temperatur", string machine = "M1") =>
        _messages.Push(new OpcMessage(machine, name, value, T0.AddMinutes(minute)));

    private StatisticsResult Query(DateTime? from = null, DateTime? to = null, int? bucketMinutes = null,
        string name = "Temperatur") =>
        _reader.Query("M1", name, from ?? T0, to ?? T0.AddHours(1), bucketMinutes);

    [Fact]
    public void Summary_contains_count_min_max_avg_first_and_last()
    {
        Add(60, 5);
        Add(80, 10);
        Add(40, 20);
        Add(70, 30);

        var result = Query();

        Assert.Equal(4, result.Count);
        Assert.Equal(40, result.Min);
        Assert.Equal(80, result.Max);
        Assert.Equal(62.5, result.Avg);
        Assert.Equal(new StatisticsPoint(T0.AddMinutes(5), 60), result.First);
        Assert.Equal(new StatisticsPoint(T0.AddMinutes(30), 70), result.Last);
        Assert.Null(result.Buckets);
        Assert.Contains("nicht zeitgewichtet", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_values_of_the_requested_machine_variable_and_period_count()
    {
        Add(60, 5);
        Add(999, 5, machine: "M2");
        Add(999, 5, name: "Drehzahl");
        Add(999, -10);
        Add(999, 90);

        var result = Query();

        Assert.Equal(1, result.Count);
        Assert.Equal(60, result.Max);
    }

    [Fact]
    public void Integer_values_are_included()
    {
        _messages.Push(new OpcMessage("M1", "Stueckzahl", 10, T0.AddMinutes(1)));
        _messages.Push(new OpcMessage("M1", "Stueckzahl", 20, T0.AddMinutes(2)));

        var result = Query(name: "Stueckzahl");

        Assert.Equal(2, result.Count);
        Assert.Equal(15, result.Avg);
    }

    [Fact]
    public void Period_without_values_gives_count_zero_and_no_numbers()
    {
        Add(60, 5);

        var result = Query(from: T0.AddHours(2), to: T0.AddHours(3));

        Assert.Equal(0, result.Count);
        Assert.Null(result.Min);
        Assert.Null(result.Avg);
        Assert.Null(result.First);
        Assert.Null(result.Last);
    }

    [Fact]
    public void Buckets_group_values_over_time_and_skip_empty_buckets()
    {
        Add(10, 1);
        Add(20, 4);
        Add(100, 12);
        Add(50, 40);

        var result = Query(bucketMinutes: 10);

        Assert.NotNull(result.Buckets);
        Assert.Equal(3, result.Buckets.Count);
        Assert.Equal(new StatisticsBucket(T0, 2, 10, 20, 15), result.Buckets[0]);
        Assert.Equal(new StatisticsBucket(T0.AddMinutes(10), 1, 100, 100, 100), result.Buckets[1]);
        Assert.Equal(new StatisticsBucket(T0.AddMinutes(40), 1, 50, 50, 50), result.Buckets[2]);
    }

    [Fact]
    public void Defaults_to_the_last_hour()
    {
        _messages.Push(new OpcMessage("M1", "Temperatur", 61.0, DateTime.UtcNow.AddMinutes(-10)));
        _messages.Push(new OpcMessage("M1", "Temperatur", 99.0, DateTime.UtcNow.AddHours(-3)));

        var result = _reader.Query("M1", "Temperatur", null, null, null);

        Assert.Equal(1, result.Count);
        Assert.Equal(61, result.Max);
    }

    [Fact]
    public void Period_longer_than_31_days_is_rejected()
    {
        Add(1, 1);

        var error = Assert.Throws<InvalidQueryException>(() => Query(from: T0, to: T0.AddDays(32)));

        Assert.Contains("31", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Period_of_exactly_31_days_is_accepted()
    {
        Add(1, 1);

        Assert.Equal(1, Query(from: T0, to: T0.AddDays(31)).Count);
    }

    [Fact]
    public void Empty_or_reversed_period_is_rejected()
    {
        Add(1, 1);

        Assert.Throws<InvalidQueryException>(() => Query(from: T0.AddHours(1), to: T0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    [InlineData(-5)]
    public void Bucket_size_outside_1_to_1440_is_rejected(int bucketMinutes)
    {
        Add(1, 1);

        Assert.Throws<InvalidQueryException>(() => Query(bucketMinutes: bucketMinutes));
    }

    [Fact]
    public void More_than_200_buckets_are_rejected_with_a_hint()
    {
        Add(1, 1);

        var error = Assert.Throws<InvalidQueryException>(() => Query(from: T0, to: T0.AddHours(4), bucketMinutes: 1));

        Assert.Contains("200", error.Message, StringComparison.Ordinal);
        Assert.Contains("bucketMinutes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exactly_200_buckets_are_accepted()
    {
        Add(1, 1);

        var result = Query(from: T0, to: T0.AddMinutes(200), bucketMinutes: 1);

        Assert.Single(result.Buckets!);
    }

    [Fact]
    public void Text_variable_is_rejected_as_not_numeric()
    {
        _messages.Push(new OpcMessage("M1", "Status", "Laufend", T0.AddMinutes(1)));

        var error = Assert.Throws<InvalidQueryException>(() => Query(name: "Status"));

        Assert.Contains("nicht numerisch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boolean_variable_is_rejected_as_not_numeric()
    {
        _messages.Push(new OpcMessage("M1", "Aktiv", true, T0.AddMinutes(1)));

        Assert.Throws<InvalidQueryException>(() => Query(name: "Aktiv"));
    }

    [Fact]
    public void Unknown_variable_is_reported_as_not_found()
    {
        Add(1, 1);

        Assert.Throws<QueryTargetNotFoundException>(() => Query(name: "Gibt-es-nicht"));
        Assert.Throws<QueryTargetNotFoundException>(() => _reader.Query("Unbekannt", "Temperatur", T0, T0.AddHours(1), null));
    }
}
