using Microsoft.Extensions.Configuration;
using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

/// <summary>Alarmlogik ohne OPC-Server: Nachrichten werden direkt an den <see cref="AlarmMonitor"/> übergeben.</summary>
public sealed class AlarmMonitorTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempDatabase _db = new();
    private readonly SqliteAlarmStore _store;
    private readonly AlarmMonitor _monitor;

    public AlarmMonitorTests()
    {
        _store = new SqliteAlarmStore(_db.Path);
        _monitor = NewMonitor(_store);
    }

    public void Dispose() => _db.Dispose();

    private static AlarmMonitor NewMonitor(SqliteAlarmStore store)
    {
        var monitor = new AlarmMonitor(store);
        monitor.Register("M1", "Temperatur", new LimitsConfig { Low = 10, High = 80 });
        return monitor;
    }

    private void Send(object? value, int minute, string name = "Temperatur") =>
        _monitor.Evaluate(new OpcMessage("M1", name, value, T0.AddMinutes(minute)));

    [Fact]
    public void Value_above_high_raises_an_alarm_with_value_limit_and_time()
    {
        Send(85.5, 1);

        var alarm = Assert.Single(_store.Active());
        Assert.Equal(AlarmKind.High, alarm.Kind);
        Assert.Equal(85.5, alarm.Value);
        Assert.Equal(80, alarm.Limit);
        Assert.Equal(T0.AddMinutes(1), alarm.RaisedAt);
        Assert.True(alarm.Active);
    }

    [Fact]
    public void Value_below_low_raises_a_low_alarm()
    {
        Send(5, 1);

        Assert.Equal(AlarmKind.Low, Assert.Single(_store.Active()).Kind);
    }

    [Fact]
    public void Value_exactly_on_the_limit_is_still_in_range()
    {
        Send(80, 1);
        Send(10, 2);

        Assert.Empty(_store.Active());
    }

    [Fact]
    public void Staying_above_the_limit_does_not_create_a_second_alarm()
    {
        Send(85, 1);
        Send(90, 2);
        Send(95, 3);

        Assert.Equal(1, _store.Query(new AlarmFilter()).Total);
        Assert.Equal(85, Assert.Single(_store.Active()).Value);
    }

    [Fact]
    public void Returning_into_range_clears_the_alarm_with_time_and_value()
    {
        Send(85, 1);
        Send(60, 5);

        Assert.Empty(_store.Active());
        var alarm = Assert.Single(_store.Query(new AlarmFilter()).Items);
        Assert.Equal(T0.AddMinutes(5), alarm.ClearedAt);
        Assert.Equal(60, alarm.ClearedValue);
        Assert.False(alarm.Active);
    }

    [Fact]
    public void A_new_violation_after_clearing_creates_a_new_alarm()
    {
        Send(85, 1);
        Send(60, 2);
        Send(90, 3);

        var all = _store.Query(new AlarmFilter()).Items;
        Assert.Equal(2, all.Count);
        Assert.Single(_store.Active());
    }

    [Fact]
    public void Jumping_from_high_to_low_ends_the_high_alarm_and_raises_a_low_alarm()
    {
        Send(85, 1);
        Send(5, 2);

        var active = Assert.Single(_store.Active());
        Assert.Equal(AlarmKind.Low, active.Kind);
        Assert.Equal(AlarmKind.High, _store.Query(new AlarmFilter(ActiveOnly: false)).Items.Last().Kind);
    }

    [Fact]
    public void Active_alarm_survives_a_restart_and_is_not_duplicated()
    {
        Send(85, 1);

        var restartedStore = new SqliteAlarmStore(_db.Path);
        var restarted = NewMonitor(restartedStore);
        restarted.Evaluate(new OpcMessage("M1", "Temperatur", 90.0, T0.AddMinutes(2)));

        Assert.Equal(1, restartedStore.Query(new AlarmFilter()).Total);
        Assert.Equal(85, Assert.Single(restartedStore.Active()).Value);

        restarted.Evaluate(new OpcMessage("M1", "Temperatur", 50.0, T0.AddMinutes(3)));
        Assert.Empty(restartedStore.Active());
    }

    [Theory]
    [InlineData("heiß")]
    [InlineData(true)]
    [InlineData(null)]
    public void Non_numeric_values_are_ignored(object? value)
    {
        Send(value, 1);

        Assert.Equal(0, _store.Query(new AlarmFilter()).Total);
    }

    [Fact]
    public void Integer_and_float_types_are_evaluated()
    {
        Send(100, 1, "Temperatur");
        _monitor.Register("M1", "Drehzahl", new LimitsConfig { High = 1600 });
        Send(1700f, 2, "Drehzahl");

        Assert.Equal(2, _store.Active().Count);
    }

    [Fact]
    public void Variable_without_limits_never_alarms()
    {
        Send(1_000_000, 1, "Stueckzahl");

        Assert.Equal(0, _store.Query(new AlarmFilter()).Total);
    }

    [Fact]
    public void Only_the_configured_bound_is_checked()
    {
        var monitor = new AlarmMonitor(_store);
        monitor.Register("M1", "Drehzahl", new LimitsConfig { High = 1600 });

        monitor.Evaluate(new OpcMessage("M1", "Drehzahl", -500, T0));

        Assert.Empty(_store.Active());
    }

    [Fact]
    public void Query_filters_by_machine_name_and_active_state()
    {
        Send(85, 1);
        Send(60, 2);
        _monitor.Evaluate(new OpcMessage("M2", "Temperatur", 99.0, T0));
        _monitor.Register("M2", "Temperatur", new LimitsConfig { High = 80 });
        _monitor.Evaluate(new OpcMessage("M2", "Temperatur", 99.0, T0.AddMinutes(3)));

        Assert.Equal(1, _store.Query(new AlarmFilter(Machine: "M1")).Total);
        Assert.Equal(1, _store.Query(new AlarmFilter(ActiveOnly: true)).Total);
        Assert.Equal(0, _store.Query(new AlarmFilter(Name: "Drehzahl")).Total);
        Assert.Equal(1, _store.Query(new AlarmFilter(From: T0.AddMinutes(2))).Total);
    }

    [Fact]
    public void PurgeCleared_removes_old_cleared_alarms_but_keeps_active_ones()
    {
        var old = DateTime.UtcNow.AddDays(-40);
        _store.Raise("M1", "Alt", AlarmKind.High, 80, 90, old);
        _store.Clear("M1", "Alt", AlarmKind.High, 50, old.AddMinutes(1));
        _store.Raise("M1", "AltAktiv", AlarmKind.High, 80, 90, old);
        _store.Raise("M1", "Neu", AlarmKind.High, 80, 90, DateTime.UtcNow);
        _store.Clear("M1", "Neu", AlarmKind.High, 50, DateTime.UtcNow);

        var removed = _store.PurgeCleared(TimeSpan.FromDays(30));

        Assert.Equal(1, removed);
        Assert.Equal(2, _store.Query(new AlarmFilter()).Total);
        Assert.Single(_store.Active());
    }

    [Theory]
    [InlineData(80, 80)]
    [InlineData(90, 10)]
    [InlineData(double.NaN, 10)]
    [InlineData(10, double.PositiveInfinity)]
    public void Invalid_limits_are_reported(double low, double high)
    {
        Assert.NotNull(new LimitsConfig { Low = low, High = high }.Validate());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(10.0, null)]
    [InlineData(null, 80.0)]
    [InlineData(10.0, 80.0)]
    public void Valid_limits_are_accepted(double? low, double? high)
    {
        Assert.Null(new LimitsConfig { Low = low, High = high }.Validate());
    }

    [Fact]
    public async Task Host_refuses_to_start_with_invalid_limits_and_names_the_variable()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Opc:Machines:0:Name"] = "Presse-1",
            ["Opc:Machines:0:Endpoint"] = "opc.tcp://127.0.0.1:1",
            ["Opc:Machines:0:Nodes:0:Name"] = "Temperatur",
            ["Opc:Machines:0:Nodes:0:NodeId"] = "ns=2;s=Presse-1.Temperatur",
            ["Opc:Machines:0:Nodes:0:Limits:Low"] = "90",
            ["Opc:Machines:0:Nodes:0:Limits:High"] = "80",
        }).Build();
        var registry = new MachineRegistry();
        var host = new OpcHostedService(registry, new SqliteMessageStore(_db.Path), new NoopHandler(), config, _monitor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));

        Assert.Contains("Presse-1.Temperatur", error.Message, StringComparison.Ordinal);
        Assert.Contains("Low", error.Message, StringComparison.Ordinal);
        Assert.Empty(registry.All);
    }

    [Fact]
    public void Limits_are_bound_from_configuration_and_shipped_settings_define_both_machines()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "client-appsettings.json"), optional: false)
            .Build();

        var machines = config.GetSection("Opc:Machines").Get<List<MachineConfig>>()!;

        Assert.Equal(["Presse-1", "Drehbank-2"], machines.Select(m => m.Name));
        foreach (var machine in machines)
        {
            Assert.Equal(["Temperatur", "Drehzahl", "Status", "Stueckzahl"], machine.Nodes.Select(n => n.Name));
            Assert.Equal(80, machine.Nodes.Single(n => n.Name == "Temperatur").Limits!.High);
            Assert.Equal(10, machine.Nodes.Single(n => n.Name == "Temperatur").Limits!.Low);
            Assert.Equal(1600, machine.Nodes.Single(n => n.Name == "Drehzahl").Limits!.High);
            Assert.All(machine.Nodes, n => Assert.Null(n.Limits?.Validate()));
        }
    }

    private sealed class NoopHandler : IMessageHandler
    {
        public Task HandleAsync(OpcMessage message, CancellationToken ct) => Task.CompletedTask;
    }
}
