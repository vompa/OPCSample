# OPCSample

Beispiel für einen OPC-UA-Client in C# mit Rx.NET, persistenter LIFO-Queue (SQLite), Serilog und integrierter REST-API mit Swagger.

## Struktur

```
OPCSample/
├── Client/
│   └── OPCClient/
│       ├── Opc/        OpcMachine, OpcSessionFactory, MachineRegistry, Hosted Service, Handler, Backoff
│       ├── Storage/    SqliteMessageStore (persistenter Stack), RetentionService
│       ├── Security/   API-Key-Authentifizierung (Rollen reader/writer)
│       ├── Mcp/        MCP-Tools für KI-Agenten
│       ├── Api/        Minimal-API-Endpunkte
│       ├── Program.cs
│       └── appsettings.json
└── Tests/
    └── OPCClient.Tests/    xUnit-Tests (ohne OPC-Server lauffähig)
```

## Starten

Voraussetzung: .NET 8 SDK und ein OPC-UA-Server (z. B. Prosys OPC UA Simulation Server).

```
cd Client/OPCClient
dotnet run
```

Maschinen, Endpoints und Nodes werden in `appsettings.json` unter `Opc:Machines` konfiguriert.

Beim Start im Development-Profil sind zwei Demo-Keys konfiguriert (`appsettings.Development.json`): `dev-reader-key` (Rolle `reader`) und `dev-writer-key` (Rolle `writer`). Ohne konfigurierten Key startet der Dienst nicht.

- Swagger UI (nur Development): http://localhost:5080/swagger
- OpenAPI-JSON: http://localhost:5080/swagger/v1/swagger.json

## Ablauf

OPC-UA-Server → `MonitoredItem.Notification` → Rx-Subject (`DistinctUntilChanged`) → SQLite (Stack/LIFO) → Worker pro Maschine → Handler (mit Timeout und Retry)

- Neueste Nachricht wird zuerst verarbeitet (LIFO).
- Nach einem Absturz werden Nachrichten im Zustand "in Arbeit" wieder auf "offen" gesetzt (at-least-once, der Handler sollte idempotent sein).
- Pro Nachricht maximal 3 Versuche mit exponentiellem Backoff (1 s, 2 s, … bis 30 s, mit Jitter), danach Zustand `Failed` (per `POST /api/messages/{id}/retry` erneut einplanen).
- Erledigte Nachrichten werden nach 7 Tagen automatisch gelöscht (`Retention`), fehlgeschlagene bleiben.
- Ausgang: standardmäßig Logausgabe, optional Webhook (`Opc:Sink`) mit stabilem `Idempotency-Key`.

## API

| Methode | Pfad | Beschreibung |
|---|---|---|
| GET | `/api/health` | Lebenszeichen: `ok` oder `degraded` (anonym, ohne Details) |
| GET | `/api/machines` | Maschinen mit Verbindungsstatus und Nachrichtenzahlen |
| GET | `/api/machines/{machine}/latest` | Aktuellster Wert je Variable |
| GET | `/api/messages` | Filter: machine, name, state, from, to, limit, offset |
| GET | `/api/messages/{id}` | Einzelne Nachricht |
| POST | `/api/messages/{id}/retry` | Fehlgeschlagene Nachricht erneut einplanen (Rolle `writer`) |

Alle Endpunkte außer `/api/health` verlangen einen API-Key (`X-Api-Key` oder `Authorization: Bearer`), siehe [Client/OPCClient/README.md](Client/OPCClient/README.md#sicherheit). Das Wiedereinplanen (`POST …/retry`) braucht die Rolle `writer`.

## KI-Agenten (MCP)

Unter `/mcp` steht ein [MCP](https://modelcontextprotocol.io)-Endpunkt bereit, über den KI-Agenten Maschinenstatus und Messwerte abfragen können (`list_machines`, `get_latest_values`, `query_messages`, `retry_message`). Es gibt bewusst kein Tool, das Werte auf der Maschine schreibt.

```
claude mcp add --transport http opc http://localhost:5080/mcp --header "X-Api-Key: dev-reader-key"
```

## Tests

```
dotnet test OPCClient.slnx
```

Die Tests laufen ohne OPC-Server (Speicher, LIFO, Retry/Backoff, Timeout, Shutdown, Zugriffsschutz, MCP). Die CI baut mit `TreatWarningsAsErrors` und prüft die Pakete auf bekannte Schwachstellen.
