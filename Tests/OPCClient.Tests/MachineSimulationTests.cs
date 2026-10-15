using OPCSimulator;

namespace OPCClient.Tests;

/// <summary>Testet die reine Wertelogik des Simulators (ohne OPC-Server, mit festem Seed).</summary>
public sealed class MachineSimulationTests
{
    private static MachineSimulation Create(
        double overheatProbability = 0, double statusChangeProbability = 0,
        string initialStatus = MachineStatus.Running, int seed = 42) =>
        new(MachineProfile.Press, new Random(seed), overheatProbability, statusChangeProbability, initialStatus);

    private static List<MachineSnapshot> Run(MachineSimulation simulation, int steps)
    {
        var snapshots = new List<MachineSnapshot>();
        for (var i = 0; i < steps; i++)
        {
            simulation.Step(1);
            snapshots.Add(simulation.Snapshot);
        }
        return snapshots;
    }

    [Fact]
    public void Running_machine_keeps_speed_in_operating_range_and_temperature_around_base()
    {
        var snapshots = Run(Create(), 300);

        Assert.All(snapshots, s =>
        {
            Assert.InRange(s.Speed, MachineProfile.Press.MinSpeed, MachineProfile.Press.MaxSpeed);
            Assert.InRange(s.Temperature, 50, 70);
        });
    }

    [Fact]
    public void Status_changes_over_time_and_only_uses_known_values()
    {
        string[] known = [MachineStatus.Running, MachineStatus.Setup, MachineStatus.Fault, MachineStatus.Off];

        var statuses = Run(Create(statusChangeProbability: 0.2), 500).Select(s => s.Status).ToHashSet();

        Assert.All(statuses, s => Assert.Contains(s, known));
        Assert.True(statuses.Count > 1, "Der Status hätte sich ändern müssen.");
    }

    [Fact]
    public void Status_stays_constant_without_change_probability()
    {
        var statuses = Run(Create(), 100).Select(s => s.Status).Distinct();

        Assert.Equal([MachineStatus.Running], statuses);
    }

    [Theory]
    [InlineData(MachineStatus.Fault)]
    [InlineData(MachineStatus.Off)]
    [InlineData(MachineStatus.Setup)]
    public void Stopped_machine_has_zero_speed_and_piece_count_stands(string status)
    {
        var simulation = Create(initialStatus: status);
        var before = simulation.Snapshot.PieceCount;

        var snapshots = Run(simulation, 50);

        Assert.All(snapshots, s =>
        {
            Assert.Equal(0, s.Speed);
            Assert.Equal(before, s.PieceCount);
            Assert.Equal(status, s.Status);
        });
    }

    [Fact]
    public void Speed_drops_to_zero_immediately_when_machine_stops()
    {
        var simulation = Create(statusChangeProbability: 1);
        simulation.Step(1);

        var snapshot = simulation.Snapshot;
        Assert.NotEqual(MachineStatus.Running, snapshot.Status);
        Assert.Equal(0, snapshot.Speed);
    }

    [Fact]
    public void Piece_count_rises_while_running()
    {
        var snapshots = Run(Create(), 60);

        Assert.True(snapshots[^1].PieceCount > snapshots[0].PieceCount);
        Assert.True(snapshots.Zip(snapshots.Skip(1), (a, b) => b.PieceCount >= a.PieceCount).All(ok => ok),
            "Die Stückzahl darf nie sinken.");
    }

    [Fact]
    public void Overheating_is_reached_above_80_degrees_and_cools_down_again()
    {
        var temperatures = Run(Create(overheatProbability: 1), 200).Select(s => s.Temperature).ToList();

        var peak = temperatures.Max();
        Assert.True(peak > 80, $"Höchste Temperatur war nur {peak} °C.");

        var afterPeak = temperatures.Skip(temperatures.IndexOf(peak));
        Assert.Contains(afterPeak, t => t < peak - 15);
    }

    [Fact]
    public void Without_overheat_probability_temperature_stays_below_alarm_level()
    {
        var peak = Run(Create(overheatProbability: 0), 1000).Max(s => s.Temperature);

        Assert.True(peak < 80, $"Höchste Temperatur war {peak} °C.");
    }

    [Fact]
    public void Same_seed_gives_same_sequence()
    {
        var first = Run(Create(0.05, 0.05, seed: 7), 200);
        var second = Run(Create(0.05, 0.05, seed: 7), 200);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Lathe_has_slightly_different_values_than_press()
    {
        var press = new MachineSimulation(MachineProfile.Press, new Random(1), 0, 0);
        var lathe = new MachineSimulation(MachineProfile.Lathe, new Random(1), 0, 0);

        Assert.NotEqual(press.Snapshot.Temperature, lathe.Snapshot.Temperature);
        Assert.Equal("Drehbank-2", lathe.Name);
    }
}
