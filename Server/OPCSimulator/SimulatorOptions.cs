namespace OPCSimulator;

/// <summary>Konfiguration des Simulators (Abschnitt "Simulator" in appsettings.json).</summary>
public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    /// <summary>Namespace-URI des Simulators. Er muss im Server den Index 2 haben.</summary>
    public const string NamespaceUri = "urn:vompa:opc-simulator";

    public int Port { get; set; } = 4840;

    /// <summary>Aktualisierungstakt der Werte in Millisekunden.</summary>
    public int UpdateIntervalMs { get; set; } = 1000;

    /// <summary>Startwert des Zufallsgenerators; ohne Wert ist der Ablauf nicht deterministisch.</summary>
    public int? Seed { get; set; }

    /// <summary>Wahrscheinlichkeit pro Takt, dass bei laufender Maschine eine Überhitzung beginnt.</summary>
    public double OverheatProbability { get; set; } = 0.01;

    /// <summary>Zusätzlich zu SecurityPolicy None auch Basic256Sha256 anbieten.</summary>
    public bool EnableSecurity { get; set; } = true;

    /// <summary>Ordner für Zertifikate (selbstsigniertes Anwendungszertifikat, Trust-Listen).</summary>
    public string PkiPath { get; set; } = "pki/simulator";

    public string EndpointUrl => $"opc.tcp://localhost:{Port}";

    public void Validate()
    {
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port muss zwischen 1 und 65535 liegen.");
        if (UpdateIntervalMs < 10)
            throw new ArgumentOutOfRangeException(nameof(UpdateIntervalMs), UpdateIntervalMs, "Takt muss mindestens 10 ms betragen.");
        if (OverheatProbability is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(OverheatProbability), OverheatProbability, "Wahrscheinlichkeit muss zwischen 0 und 1 liegen.");
        if (string.IsNullOrWhiteSpace(PkiPath))
            throw new ArgumentException("PkiPath darf nicht leer sein.", nameof(PkiPath));
    }
}
