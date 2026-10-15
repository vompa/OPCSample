using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OPCClient.Opc;
using OPCClient.Security;
using OPCClient.Storage;

namespace OPCClient.Mcp;

/// <summary>
/// Die Tools, die ein KI-Agent über MCP sieht: Lesezugriff auf Maschinenstatus und Messwerte sowie ein einziges,
/// bewusst eng gefasstes Schreib-Tool. Es gibt absichtlich KEIN Tool, das Werte auf der Maschine (OPC-UA-Server) schreibt.
/// Audit-Log und Rollenprüfung für schreibende Tools übernimmt <see cref="McpAuditFilter"/> für jeden Aufruf.
/// </summary>
[McpServerToolType]
public sealed class OpcTools(
    MachineRegistry registry, SqliteMessageStore store, SqliteAlarmStore alarms, SqliteStatisticsReader statistics,
    IHttpContextAccessor httpContext)
{
    public const int MaxLimit = 100;
    public const int DefaultLimit = 25;

    public static string WriterRequiredMessage(string tool) =>
        $"Keine Berechtigung: {tool} erfordert einen API-Key mit der Rolle 'writer'.";

    [McpServerTool(Name = "list_machines", ReadOnly = true, Idempotent = true)]
    [Description("Listet alle Maschinen mit Verbindungsstatus zum OPC-Server und der Zahl der Nachrichten je Zustand " +
                 "(Pending, Processing, Done, Failed). Das ist der richtige erste Schritt. 'connected = false' heißt: " +
                 "Der OPC-Server ist gerade nicht erreichbar, die zuletzt gespeicherten Werte sind dann möglicherweise veraltet.")]
    public IReadOnlyList<MachineDto> ListMachines()
    {
        var names = registry.All.Select(m => m.Name).Concat(store.MachineNames()).Distinct().Order();
        return names.Select(n => new MachineDto(n, registry.Get(n)?.IsConnected ?? false, store.Stats(n))).ToList();
    }

    [McpServerTool(Name = "get_latest_values", ReadOnly = true, Idempotent = true)]
    [Description("Liefert den aktuellsten gespeicherten Wert je Variable einer Maschine (z. B. Temperatur, Status) mit Zeitstempel. " +
                 "Der Zeitstempel zeigt, wie aktuell der Wert wirklich ist.")]
    public IReadOnlyList<MessageDto> GetLatestValues(
        [Description("Name der Maschine, wie ihn list_machines liefert.")] string machine)
    {
        EnsureKnown(machine);
        return store.Latest(machine);
    }

    [McpServerTool(Name = "query_messages", ReadOnly = true, Idempotent = true)]
    [Description("Sucht gespeicherte Wertänderungen, neueste zuerst. Alle Filter sind optional. Das Ergebnis ist seitenweise " +
                 "(limit/offset, höchstens 100 pro Seite); 'total' ist die Gesamtzahl der Treffer. " +
                 "Nutze den Zustand 'Failed', um nicht zustellbare Nachrichten zu finden.")]
    public PagedResult<MessageDto> QueryMessages(
        [Description("Maschinenname.")] string? machine = null,
        [Description("Variablenname, z. B. Temperatur.")] string? name = null,
        [Description("Zustand: Pending, Processing, Done oder Failed.")] string? state = null,
        [Description("Nur Nachrichten ab diesem Zeitpunkt (ISO 8601, UTC).")] DateTime? from = null,
        [Description("Nur Nachrichten bis zu diesem Zeitpunkt (ISO 8601, UTC).")] DateTime? to = null,
        [Description("Treffer pro Seite, 1 bis 100.")] int limit = DefaultLimit,
        [Description("Anzahl zu überspringender Treffer für die nächste Seite.")] int offset = 0)
    {
        MessageState? parsedState = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<MessageState>(state, ignoreCase: true, out var s) || !Enum.IsDefined(s))
                throw new McpException($"Unbekannter Zustand '{state}'. Gültig: {string.Join(", ", Enum.GetNames<MessageState>())}.");
            parsedState = s;
        }

        return store.Query(new MessageFilter(machine, name, parsedState, from, to, Math.Clamp(limit, 1, MaxLimit), offset));
    }

    [McpServerTool(Name = "retry_message", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Plant eine fehlgeschlagene Nachricht (Zustand Failed) erneut zur Zustellung ein. Nur mit API-Key der Rolle 'writer'. " +
                 "Die Zustellung ist at-least-once: Der Empfänger kann die Nachricht daher mehrfach sehen. " +
                 "Prüfe vorher mit query_messages (state = Failed), dass die Ursache behoben ist.")]
    public string RetryMessage([Description("Id der Nachricht aus query_messages.")] long id)
    {
        // Zweite Absicherung neben dem Key-Handler: Schreibrechte werden pro Tool-Aufruf geprüft
        if (httpContext.HttpContext?.User.IsInRole(Roles.Writer) != true)
            throw new McpException(WriterRequiredMessage("retry_message"));

        var machine = store.Requeue(id)
            ?? throw new McpException($"Nachricht {id} nicht gefunden oder nicht im Zustand Failed.");
        registry.Get(machine)?.Wake();
        return $"Nachricht {id} wurde für Maschine '{machine}' erneut eingeplant.";
    }

    [McpServerTool(Name = "get_active_alarms", ReadOnly = true, Idempotent = true)]
    [Description("Listet alle gerade aktiven Alarme (Grenzwertverletzungen 'High' oder 'Low') mit auslösendem Wert, verletzter Grenze " +
                 "und Auslösezeitpunkt, optional nur für eine Maschine. Eine leere Liste heißt: alle überwachten Werte sind im Bereich. " +
                 "Nächster Schritt: get_latest_values für den aktuellen Wert, query_alarms für den Verlauf bereits beendeter Alarme.")]
    public IReadOnlyList<AlarmDto> GetActiveAlarms(
        [Description("Maschinenname, wie ihn list_machines liefert. Ohne Angabe: alle Maschinen.")] string? machine = null)
    {
        if (machine is not null) EnsureKnown(machine);
        return alarms.Active(machine);
    }

    [McpServerTool(Name = "query_alarms", ReadOnly = true, Idempotent = true)]
    [Description("Sucht Alarme (aktive und beendete), neueste zuerst. Alle Filter sind optional. Das Ergebnis ist seitenweise " +
                 "(limit/offset, höchstens 100 pro Seite); 'total' ist die Gesamtzahl der Treffer. 'from' und 'to' beziehen sich auf den " +
                 "Auslösezeitpunkt. Beendete Alarme werden nach einigen Tagen gelöscht. " +
                 "Nächster Schritt: get_statistics oder query_messages, um den Verlauf der betroffenen Variable zu prüfen.")]
    public PagedResult<AlarmDto> QueryAlarms(
        [Description("Maschinenname.")] string? machine = null,
        [Description("Variablenname, z. B. Temperatur.")] string? name = null,
        [Description("Nur Alarme, die ab diesem Zeitpunkt ausgelöst wurden (ISO 8601, UTC).")] DateTime? from = null,
        [Description("Nur Alarme, die bis zu diesem Zeitpunkt ausgelöst wurden (ISO 8601, UTC).")] DateTime? to = null,
        [Description("true = nur noch aktive Alarme.")] bool activeOnly = false,
        [Description("Treffer pro Seite, 1 bis 100.")] int limit = DefaultLimit,
        [Description("Anzahl zu überspringender Treffer für die nächste Seite.")] int offset = 0) =>
        alarms.Query(new AlarmFilter(machine, name, from, to, activeOnly, Math.Clamp(limit, 1, MaxLimit), offset));

    [McpServerTool(Name = "get_statistics", ReadOnly = true, Idempotent = true)]
    [Description("Berechnet Statistik einer numerischen Variable einer Maschine: Anzahl, Minimum, Maximum, Mittelwert sowie ersten und " +
                 "letzten Wert mit Zeitpunkt. Mit 'bucketMinutes' (1 bis 1440) zusätzlich eine Zeitreihe mit Anzahl, Minimum, Maximum und " +
                 "Mittelwert je Zeitabschnitt (höchstens 200 Abschnitte, leere werden weggelassen). Standardzeitraum: die letzte Stunde, " +
                 "höchstens 31 Tage. WICHTIG: Gespeichert werden nur Wertänderungen. Die Statistik gilt daher für Änderungen, nicht " +
                 "zeitgewichtet; ein lange konstanter Wert zählt nur einmal und der Mittelwert ist kein Zeitmittel. " +
                 "Nächster Schritt: query_alarms, wenn Grenzwertverletzungen auffallen.")]
    public StatisticsResult GetStatistics(
        [Description("Maschinenname, wie ihn list_machines liefert.")] string machine,
        [Description("Name einer numerischen Variable, z. B. Temperatur (siehe get_latest_values).")] string name,
        [Description("Beginn des Zeitraums (ISO 8601, UTC). Standard: eine Stunde vor 'to'.")] DateTime? from = null,
        [Description("Ende des Zeitraums (ISO 8601, UTC). Standard: jetzt.")] DateTime? to = null,
        [Description("Größe der Zeitabschnitte in Minuten (1 bis 1440) für eine Zeitreihe. Ohne Angabe nur die Gesamtstatistik.")] int? bucketMinutes = null)
    {
        EnsureKnown(machine);
        try
        {
            return statistics.Query(machine, name, from, to, bucketMinutes);
        }
        catch (Exception ex) when (ex is InvalidQueryException or QueryTargetNotFoundException)
        {
            throw new McpException(ex.Message);
        }
    }

    private void EnsureKnown(string machine)
    {
        if (registry.Get(machine) is null && !store.MachineNames().Contains(machine))
            throw new McpException($"Unbekannte Maschine '{machine}'. Rufe zuerst list_machines auf.");
    }
}
