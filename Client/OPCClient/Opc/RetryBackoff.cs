namespace OPCClient.Opc;

/// <summary>
/// Exponentielles Backoff mit Jitter für Wiederholungen des Handlers. Sofortige Wiederholungen würden einen
/// gerade überlasteten Zielservice zusätzlich belasten und alle Maschinen im Gleichschritt anfragen lassen.
/// </summary>
public static class RetryBackoff
{
    public static readonly TimeSpan Base = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    /// <summary>Anteil, um den die Wartezeit zufällig nach oben oder unten abweichen darf.</summary>
    public const double JitterFactor = 0.2;

    /// <param name="attempt">Nummer des gerade fehlgeschlagenen Versuchs (ab 1).</param>
    /// <param name="jitter">-1 bis 1; 0 = genau der Basiswert (für Tests).</param>
    public static TimeSpan Delay(int attempt, double jitter = 0)
    {
        var exponent = Math.Clamp(attempt, 1, 30) - 1;
        var seconds = Math.Min(Base.TotalSeconds * Math.Pow(2, exponent), Max.TotalSeconds);
        var withJitter = seconds * (1 + Math.Clamp(jitter, -1, 1) * JitterFactor);
        return TimeSpan.FromSeconds(Math.Min(withJitter, Max.TotalSeconds));
    }

    public static TimeSpan Next(int attempt) => Delay(attempt, Random.Shared.NextDouble() * 2 - 1);
}
