namespace OPCSimulator;

/// <summary>Mögliche Werte der Variable "Status".</summary>
public static class MachineStatus
{
    public const string Running = "Läuft";
    public const string Setup = "Rüsten";
    public const string Fault = "Störung";
    public const string Off = "Aus";

    public static bool IsStopped(string status) => status != Running;
}
