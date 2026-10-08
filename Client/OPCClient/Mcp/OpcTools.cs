using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OPCClient.Opc;
using OPCClient.Security;
using OPCClient.Storage;
using Serilog;

namespace OPCClient.Mcp;

/// <summary>
/// Die Tools, die ein KI-Agent über MCP sieht: Lesezugriff auf Maschinenstatus und Messwerte sowie ein einziges,
/// bewusst eng gefasstes Schreib-Tool. Es gibt absichtlich KEIN Tool, das Werte auf der Maschine (OPC-UA-Server) schreibt.
/// </summary>
[McpServerToolType]
public sealed class OpcTools(MachineRegistry registry, SqliteMessageStore store, IHttpContextAccessor httpContext)
{
    public const int MaxLimit = 100;
    public const int DefaultLimit = 25;

    private string Client => httpContext.HttpContext?.User.Identity?.Name ?? "unbekannt";

    [McpServerTool(Name = "list_machines", ReadOnly = true, Idempotent = true)]
    [Description("Listet alle Maschinen mit Verbindungsstatus zum OPC-Server und der Zahl der Nachrichten je Zustand " +
                 "(Pending, Processing, Done, Failed). Das ist der richtige erste Schritt. 'connected = false' heißt: " +
                 "Der OPC-Server ist gerade nicht erreichbar, die zuletzt gespeicherten Werte sind dann möglicherweise veraltet.")]
    public IReadOnlyList<MachineDto> ListMachines()
    {
        Audit("list_machines", null);
        var names = registry.All.Select(m => m.Name).Concat(store.MachineNames()).Distinct().Order();
        return names.Select(n => new MachineDto(n, registry.Get(n)?.IsConnected ?? false, store.Stats(n))).ToList();
    }

    [McpServerTool(Name = "get_latest_values", ReadOnly = true, Idempotent = true)]
    [Description("Liefert den aktuellsten gespeicherten Wert je Variable einer Maschine (z. B. Temperatur, Status) mit Zeitstempel. " +
                 "Der Zeitstempel zeigt, wie aktuell der Wert wirklich ist.")]
    public IReadOnlyList<MessageDto> GetLatestValues(
        [Description("Name der Maschine, wie ihn list_machines liefert.")] string machine)
    {
        Audit("get_latest_values", machine);
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
        Audit("query_messages", $"machine={machine} name={name} state={state} from={from:o} to={to:o} limit={limit} offset={offset}");

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
        Audit("retry_message", id.ToString());

        // Zweite Absicherung neben dem Key-Handler: Schreibrechte werden pro Tool-Aufruf geprüft
        if (httpContext.HttpContext?.User.IsInRole(Roles.Writer) != true)
            throw new McpException("Keine Berechtigung: retry_message erfordert einen API-Key mit der Rolle 'writer'.");

        var machine = store.Requeue(id)
            ?? throw new McpException($"Nachricht {id} nicht gefunden oder nicht im Zustand Failed.");
        registry.Get(machine)?.Wake();
        return $"Nachricht {id} wurde für Maschine '{machine}' erneut eingeplant.";
    }

    private void EnsureKnown(string machine)
    {
        if (registry.Get(machine) is null && !store.MachineNames().Contains(machine))
            throw new McpException($"Unbekannte Maschine '{machine}'. Rufe zuerst list_machines auf.");
    }

    /// <summary>Audit-Log: wer hat welches Tool mit welchen Argumenten aufgerufen (auf 300 Zeichen gekürzt).</summary>
    private void Audit(string tool, string? arguments)
    {
        var shortened = arguments is { Length: > 300 } ? arguments[..300] + "..." : arguments;
        Log.Information("MCP: Client {Client} ruft {Tool} auf: {Arguments}", Client, tool, shortened);
    }
}
