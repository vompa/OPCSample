using System.Collections.Concurrent;
using OPCClient.Storage;
using Serilog;

namespace OPCClient.Opc;

/// <summary>Grenzwerte einer Variable; jede Grenze ist optional. Ein Wert genau auf der Grenze gilt als im Bereich.</summary>
public sealed class LimitsConfig
{
    public double? Low { get; set; }
    public double? High { get; set; }

    /// <summary>Fehlertext, wenn die Grenzwerte unbrauchbar sind (NaN/unendlich oder Low >= High); sonst null.</summary>
    public string? Validate()
    {
        if (Low is { } low && !double.IsFinite(low)) return "Low ist keine endliche Zahl.";
        if (High is { } high && !double.IsFinite(high)) return "High ist keine endliche Zahl.";
        if (Low is { } l && High is { } h && l >= h) return $"Low ({l}) muss kleiner als High ({h}) sein.";
        return null;
    }
}

/// <summary>
/// Prüft neu gespeicherte Werte gegen die Grenzwerte und führt die Alarme in der Datenbank nach.
/// Bewusst ohne OPC-Abhängigkeit, damit es ohne Server testbar ist. Der Zustand liegt nur in der Datenbank:
/// ein aktiver Alarm bleibt nach einem Neustart aktiv, und Auslösen bzw. Beenden sind idempotent.
/// </summary>
public sealed class AlarmMonitor(SqliteAlarmStore store)
{
    private readonly ConcurrentDictionary<(string Machine, string Name), LimitsConfig> _limits = new();

    /// <summary>Registriert die Grenzwerte einer Variable (vor dem Start der Maschine aufrufen).</summary>
    public void Register(string machine, string name, LimitsConfig limits) => _limits[(machine, name)] = limits;

    /// <summary>Wertet eine gespeicherte Änderung aus. Nicht numerische Werte und Variablen ohne Grenzwerte werden ignoriert.</summary>
    public void Evaluate(OpcMessage message)
    {
        if (!_limits.TryGetValue((message.Machine, message.Name), out var limits)) return;
        if (!NumericValue.TryGet(message.Value, out var value)) return;

        if (limits.High is { } high) Update(message, value, AlarmKind.High, high, value > high);
        if (limits.Low is { } low) Update(message, value, AlarmKind.Low, low, value < low);
    }

    private void Update(OpcMessage message, double value, AlarmKind kind, double limit, bool violated)
    {
        var log = Log.ForContext("Machine", message.Machine);

        if (violated)
        {
            if (store.Raise(message.Machine, message.Name, kind, limit, value, message.Timestamp))
                log.Warning("Alarm {Kind}: {Name}={Value} überschreitet Grenze {Limit}", kind, message.Name, value, limit);
        }
        else if (store.Clear(message.Machine, message.Name, kind, value, message.Timestamp))
        {
            log.Information("Alarm {Kind} beendet: {Name}={Value} wieder im Bereich", kind, message.Name, value);
        }
    }
}

/// <summary>Wandelt die numerischen OPC-Typen in <see cref="double"/>. Bool, Text und Arrays zählen nicht als Zahl.</summary>
public static class NumericValue
{
    public static bool TryGet(object? value, out double number)
    {
        number = value switch
        {
            byte x => x,
            sbyte x => x,
            short x => x,
            ushort x => x,
            int x => x,
            uint x => x,
            long x => x,
            ulong x => x,
            float x => x,
            double x => x,
            _ => double.NaN,
        };
        return double.IsFinite(number);
    }
}
