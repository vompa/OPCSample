namespace OPCSimulator;

/// <summary>Momentaufnahme der vier Variablen einer Maschine.</summary>
public sealed record MachineSnapshot(double Temperature, double Speed, string Status, int PieceCount);
