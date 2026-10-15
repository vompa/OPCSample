namespace OPCSimulator;

/// <summary>Kennwerte einer simulierten Maschine.</summary>
/// <param name="Name">Name des Ordners unter Objects (z. B. "Presse-1").</param>
/// <param name="BaseTemperature">Grundtemperatur im Betrieb in °C.</param>
/// <param name="TemperatureAmplitude">Ausschlag der langsamen Sinuskurve in °C.</param>
/// <param name="MinSpeed">Untere Drehzahl im Betrieb in U/min.</param>
/// <param name="MaxSpeed">Obere Drehzahl im Betrieb in U/min.</param>
public sealed record MachineProfile(
    string Name, double BaseTemperature, double TemperatureAmplitude, double MinSpeed, double MaxSpeed)
{
    public static readonly MachineProfile Press = new("Presse-1", 60, 3, 1200, 1500);
    public static readonly MachineProfile Lathe = new("Drehbank-2", 58, 4, 1250, 1450);

    public static IReadOnlyList<MachineProfile> Defaults { get; } = [Press, Lathe];
}
