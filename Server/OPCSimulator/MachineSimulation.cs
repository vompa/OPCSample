namespace OPCSimulator;

/// <summary>
/// Reine Wertelogik einer Maschine (ohne OPC-Abhängigkeit): Temperatur, Drehzahl, Status und Stückzahl.
/// Mit festem Seed und festem Takt ist der Ablauf deterministisch.
/// </summary>
public sealed class MachineSimulation
{
    private const double TemperatureWavePeriodSeconds = 300;
    private const double SpeedWavePeriodSeconds = 120;
    private const double StoppedTemperature = 30;
    private const double SetupTemperatureDrop = 8;
    private const double OverheatPeakOffset = 28;
    private const double HeatingRatePerSecond = 6;
    private const double CoolingRatePerSecond = 2;
    private const double SpeedRampPerSecond = 400;
    private const double SpeedPerPiecePerSecond = 600;
    private const double TemperatureNoise = 0.5;

    private readonly MachineProfile _profile;
    private readonly Random _random;
    private readonly double _overheatProbability;
    private readonly double _statusChangeProbability;

    private double _elapsedSeconds;
    private double _baseLevel;
    private double _temperature;
    private double _speed;
    private double _pieceFraction;
    private int _pieceCount;
    private OverheatPhase _phase = OverheatPhase.None;
    private double _overheatOffset;
    private double _holdRemainingSeconds;
    private string _status;

    public MachineSimulation(
        MachineProfile profile, Random random, double overheatProbability,
        double statusChangeProbability = 0.01, string initialStatus = MachineStatus.Running)
    {
        _profile = profile;
        _random = random;
        _overheatProbability = overheatProbability;
        _statusChangeProbability = statusChangeProbability;
        _status = initialStatus;
        _baseLevel = TargetBaseLevel();
        _speed = MachineStatus.IsStopped(_status) ? 0 : (profile.MinSpeed + profile.MaxSpeed) / 2;
        _temperature = ComputeTemperature();
    }

    public string Name => _profile.Name;

    public MachineSnapshot Snapshot => new(_temperature, _speed, _status, _pieceCount);

    /// <summary>Rechnet die Simulation um <paramref name="elapsedSeconds"/> weiter.</summary>
    public void Step(double elapsedSeconds)
    {
        _elapsedSeconds += elapsedSeconds;
        UpdateStatus();
        UpdateOverheat(elapsedSeconds);
        _baseLevel += (TargetBaseLevel() - _baseLevel) * Math.Min(1, elapsedSeconds / 30);
        UpdateSpeed(elapsedSeconds);
        UpdatePieceCount(elapsedSeconds);
        _temperature = ComputeTemperature();
    }

    private void UpdateStatus()
    {
        if (_random.NextDouble() >= _statusChangeProbability) return;
        _status = NextStatus(_status);
    }

    private string NextStatus(string current)
    {
        if (current == MachineStatus.Running)
        {
            var roll = _random.NextDouble();
            return roll < 0.5 ? MachineStatus.Setup : roll < 0.8 ? MachineStatus.Fault : MachineStatus.Off;
        }

        if (_random.NextDouble() < 0.7) return MachineStatus.Running;
        string[] stopped = [MachineStatus.Setup, MachineStatus.Fault, MachineStatus.Off];
        var others = stopped.Where(s => s != current).ToArray();
        return others[_random.Next(others.Length)];
    }

    private void UpdateOverheat(double elapsedSeconds)
    {
        var running = _status == MachineStatus.Running;

        // Steht die Maschine, kühlt eine laufende Überhitzung nur noch ab
        if (!running && _phase is OverheatPhase.Heating or OverheatPhase.Holding)
            _phase = OverheatPhase.Cooling;

        switch (_phase)
        {
            case OverheatPhase.None when running && _random.NextDouble() < _overheatProbability:
                _phase = OverheatPhase.Heating;
                break;
            case OverheatPhase.Heating:
                _overheatOffset = Math.Min(OverheatPeakOffset, _overheatOffset + HeatingRatePerSecond * elapsedSeconds);
                if (_overheatOffset >= OverheatPeakOffset)
                {
                    _phase = OverheatPhase.Holding;
                    _holdRemainingSeconds = 8 + _random.NextDouble() * 8;
                }
                break;
            case OverheatPhase.Holding:
                _holdRemainingSeconds -= elapsedSeconds;
                if (_holdRemainingSeconds <= 0) _phase = OverheatPhase.Cooling;
                break;
            case OverheatPhase.Cooling:
                _overheatOffset = Math.Max(0, _overheatOffset - CoolingRatePerSecond * elapsedSeconds);
                if (_overheatOffset == 0) _phase = OverheatPhase.None;
                break;
        }
    }

    private double TargetBaseLevel() => _status switch
    {
        MachineStatus.Running => _profile.BaseTemperature,
        MachineStatus.Setup => _profile.BaseTemperature - SetupTemperatureDrop,
        _ => StoppedTemperature
    };

    private double ComputeTemperature()
    {
        var wave = _status == MachineStatus.Running
            ? _profile.TemperatureAmplitude * Math.Sin(2 * Math.PI * _elapsedSeconds / TemperatureWavePeriodSeconds)
            : 0;
        var noise = (_random.NextDouble() - 0.5) * 2 * TemperatureNoise;
        return Math.Round(_baseLevel + wave + _overheatOffset + noise, 2);
    }

    private void UpdateSpeed(double elapsedSeconds)
    {
        if (MachineStatus.IsStopped(_status))
        {
            _speed = 0;
            return;
        }

        var middle = (_profile.MinSpeed + _profile.MaxSpeed) / 2;
        var halfRange = (_profile.MaxSpeed - _profile.MinSpeed) / 2;
        var wave = 0.6 * halfRange * Math.Sin(2 * Math.PI * _elapsedSeconds / SpeedWavePeriodSeconds);
        var noise = (_random.NextDouble() - 0.5) * 0.2 * halfRange;
        var target = Math.Clamp(middle + wave + noise, _profile.MinSpeed, _profile.MaxSpeed);

        _speed = _speed < target ? Math.Min(target, _speed + SpeedRampPerSecond * elapsedSeconds) : target;
        _speed = Math.Round(_speed, 1);
    }

    private void UpdatePieceCount(double elapsedSeconds)
    {
        if (_status != MachineStatus.Running) return;

        _pieceFraction += _speed / SpeedPerPiecePerSecond * elapsedSeconds;
        var whole = (int)_pieceFraction;
        _pieceCount += whole;
        _pieceFraction -= whole;
    }

    private enum OverheatPhase { None, Heating, Holding, Cooling }
}
