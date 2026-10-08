# OPCClient

ASP.NET-Core-Dienst (.NET 8), der Werte von OPC-UA-Servern (Maschinen) abonniert, sie in einer lokalen SQLite-Datenbank zwischenspeichert und nacheinander an einen Handler übergibt. Über eine kleine REST-API (mit Swagger) lassen sich Maschinen, Nachrichten und Fehlschläge einsehen.

## Funktionsweise

```
OPC-UA-Server ──Subscription──▶ Rx-Stream ──DistinctUntilChanged──▶ SQLite (Stack) ──▶ Worker ──▶ Handler
```

- **Eine Maschine = eine `OpcMachine`**: eigene OPC-UA-Session, eigener Stream, eigener Worker.
- **Nur echte Änderungen**: Gleiche Werte (gleicher Name und Wert) hintereinander werden verworfen.
- **Persistent**: Jede Änderung landet zuerst in SQLite. Offene Nachrichten überleben einen Neustart.
- **LIFO**: Der Worker verarbeitet die *neueste* Nachricht zuerst.
- **Retry mit Backoff**: Schlägt der Handler fehl oder überschreitet er das Timeout, wird die Nachricht bis zu 3-mal versucht (Wartezeit wächst exponentiell, mit Jitter), danach gilt sie als `Failed`. Während der Wartezeit werden andere Nachrichten weiter verarbeitet.
- **Aufräumen**: Erledigte Nachrichten werden nach 7 Tagen gelöscht (`Retention`), `Failed` bleiben bis zum Retry.
- **OPC-Server optional**: Der Dienst startet auch ohne erreichbaren Server. Die Verbindung wird im Hintergrund alle 10 Sekunden neu versucht. Bis dahin laufen Worker, API und das Abarbeiten offener Nachrichten aus der DB normal weiter. Ein Verbindungsabbruch im laufenden Betrieb wird automatisch wiederhergestellt.

### Nachrichtenstatus

| Status       | Bedeutung                                   |
|--------------|---------------------------------------------|
| `Pending`    | Wartet auf Verarbeitung                     |
| `Processing` | Wird gerade vom Handler verarbeitet         |
| `Done`       | Erfolgreich verarbeitet                     |
| `Failed`     | Nach 3 Versuchen endgültig fehlgeschlagen   |

Nachrichten im Status `Processing` werden beim Start wieder auf `Pending` gesetzt (z. B. nach einem Absturz).

## Voraussetzungen

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Optional: ein OPC-UA-Server (Standard: `opc.tcp://localhost:4840`)

## Starten

Aus dem Ordner `Client/OPCClient`:

```bash
dotnet run
```

Oder die Solution `OPCClient.slnx` im Repository-Root in Visual Studio öffnen und starten.

Standardmäßig lauscht der Dienst auf `http://localhost:5080` (Einstellung `Urls`, hat Vorrang vor den Ports in `launchSettings.json`).

- Swagger-UI: <http://localhost:5080/swagger>
- Health: <http://localhost:5080/api/health>

Build der gesamten Solution:

```bash
dotnet build OPCClient.slnx
```

## Konfiguration

Alles steht in `appsettings.json`:

```json
{
  "Urls": "http://localhost:5080",
  "Database": { "Path": "opc.db" },
  "Opc": {
    "HandlerTimeoutSeconds": 5,
    "Machines": [
      {
        "Name": "Presse-1",
        "Endpoint": "opc.tcp://localhost:4840",
        "Nodes": [
          { "Name": "Temperatur", "NodeId": "ns=2;s=Temperatur" },
          { "Name": "Status", "NodeId": "ns=2;s=Status" }
        ]
      }
    ]
  }
}
```

| Einstellung                | Bedeutung                                                      |
|----------------------------|----------------------------------------------------------------|
| `Urls`                     | Adresse der HTTP-API                                           |
| `Database:Path`            | Pfad der SQLite-Datei                                          |
| `Opc:HandlerTimeoutSeconds`| Maximale Laufzeit des Handlers pro Nachricht                   |
| `Opc:Sink:Type`            | `Log` (Standard) oder `Webhook`                                |
| `Opc:Sink:WebhookUrl`      | Ziel des Webhooks, nur HTTPS (HTTP nur für localhost)          |
| `Opc:AutoAcceptUntrustedCertificates` | Unbekannte Serverzertifikate akzeptieren (Standard `false`) |
| `Retention:DoneRetentionDays` | Erledigte Nachrichten älter als N Tage löschen (Standard 7)  |
| `Retention:IntervalMinutes`   | Prüfintervall des Aufräumens (Standard 60)                   |
| `Authentication:ApiKeys[]`  | `Name`, `KeySha256`, `Role` (`reader`/`writer`)                |
| `RateLimit:RequestsPerMinute` | Anfragen pro Minute und Client (Standard 120)                |
| `Machines[].UseSecurity`   | OPC-Endpoint mit Signierung und Verschlüsselung (Standard `false`) |
| `Opc:Machines[]`           | Liste der Maschinen (beliebig viele, auch leer)                |
| `Machines[].Name`          | Eindeutiger Name, wird in API, DB und Logs verwendet           |
| `Machines[].Endpoint`      | OPC-UA-Endpoint-URL                                            |
| `Machines[].Nodes[]`       | Zu überwachende Variablen (`Name` + `NodeId`)                  |

Einstellungen lassen sich auch per Umgebungsvariable überschreiben, z. B. `Opc__HandlerTimeoutSeconds=10`.

## REST-API

Basis: `/api`

| Methode | Pfad                              | Beschreibung                                                                 |
|---------|-----------------------------------|------------------------------------------------------------------------------|
| GET     | `/health`                         | `ok` oder `degraded` (eine Maschine nicht verbunden); öffentlich, ohne Details |
| GET     | `/machines`                       | Alle Maschinen mit Verbindungsstatus (`connected`) und Nachrichtenzahlen     |
| GET     | `/machines/{machine}/latest`      | Aktuellster Wert je Variable                                                 |
| GET     | `/messages`                       | Nachrichten filtern, neueste zuerst                                          |
| GET     | `/messages/{id}`                  | Einzelne Nachricht                                                           |
| POST    | `/messages/{id}/retry`            | Fehlgeschlagene Nachricht erneut einplanen (Rolle `writer`)                  |

Filter für `/messages` (Query-Parameter): `machine`, `name`, `state`, `from`, `to`, `limit` (Standard 100), `offset`.

Beispiele:

```bash
curl -H "X-Api-Key: dev-reader-key" http://localhost:5080/api/machines
curl -H "X-Api-Key: dev-reader-key" "http://localhost:5080/api/messages?machine=Presse-1&state=Failed&limit=20"
curl -X POST -H "X-Api-Key: dev-writer-key" http://localhost:5080/api/messages/42/retry
```

## Eigene Verarbeitung einbauen

Die Verarbeitung ist über `IMessageHandler` (`Opc/MessageHandlers.cs`) austauschbar:

- `LoggingMessageHandler` (Standard) schreibt jeden Wert ins Log.
- `WebhookMessageHandler` (`Opc:Sink:Type = Webhook`) sendet jede Nachricht als JSON per `POST` an `Opc:Sink:WebhookUrl`. Jede Anfrage trägt einen stabilen `Idempotency-Key` (`<Maschine>-<Nachrichten-Id>`), an dem der Empfänger Duplikate erkennt. Antworten außerhalb von 2xx lösen einen Retry aus.
- Eigene Logik: `IMessageHandler` implementieren und in `Program.cs` registrieren.

Regeln für jeden Handler:

- Den übergebenen `CancellationToken` immer weiterreichen, sonst greift das Timeout nicht.
- Eine Exception oder ein Timeout löst einen Retry aus (Wartezeit 1 s, 2 s, 4 s … bis höchstens 30 s, mit Jitter; maximal 3 Versuche).
- Der Handler muss idempotent sein, da eine Nachricht bei Fehlern mehrfach ankommen kann.

## Projektstruktur

```
OPCClient/
├── Program.cs                 Start, Logging, Dependency Injection, Swagger
├── appsettings.json           Konfiguration
├── Api/
│   └── OpcApi.cs              REST-Endpunkte
├── Opc/
│   ├── OpcHostedService.cs    Startet alle Maschinen
│   ├── OpcMachine.cs          Session, Subscription, Rx-Stream, Worker, Reconnect
│   ├── OpcSessionFactory.cs   Aufbau der OPC-UA-Session (Sicherheit, Zertifikate)
│   ├── MachineRegistry.cs     Verzeichnis der laufenden Maschinen
│   ├── MessageHandlers.cs     IMessageHandler: Log und Webhook
│   ├── MessageValueComparer.cs  Vergleich für DistinctUntilChanged (auch Arrays)
│   ├── RetryBackoff.cs        Wartezeiten zwischen Versuchen
│   └── OpcMessage.cs          Nachrichtenmodell
├── Storage/
│   ├── SqliteMessageStore.cs  Persistenter Stack in SQLite
│   ├── RetentionService.cs    Löscht erledigte Nachrichten nach Frist
│   └── Models.cs              DTOs, Filter, Status
├── Security/                  API-Key-Handler, Rollen, Hashing
└── Mcp/
    └── OpcTools.cs            MCP-Tools für KI-Agenten
```

## Laufzeitdateien

Werden beim Start im Arbeitsverzeichnis angelegt:

- `opc.db` (plus `-wal`/`-shm`): Nachrichtenspeicher
- `logs/opc-<Datum>.log`: Logdateien, 14 Tage aufbewahrt
- `pki/`: OPC-UA-Zertifikate (eigenes Zertifikat, vertraute und abgelehnte)

## Sicherheit

### API und MCP

- Jeder Endpunkt außer `/api/health` verlangt einen **API-Key** (`X-Api-Key: <key>` oder `Authorization: Bearer <key>`). Das gilt für die REST-API und für `/mcp`.
- Zwei Rollen: `reader` (lesen) und `writer` (zusätzlich `POST /api/messages/{id}/retry` bzw. das MCP-Tool `retry_message`).
- Konfiguriert wird nur der **SHA-256-Hash** eines Keys, nie der Klartext. Ohne gültig konfigurierten Key startet der Dienst nicht.
- Pro Client (bzw. pro IP bei fehlgeschlagener Anmeldung) gilt ein Rate Limit (`RateLimit:RequestsPerMinute`, Standard 120).
- Swagger ist nur im Development-Profil aktiv.

Eigenen Key erzeugen und eintragen:

```bash
dotnet run -- hash-key "$(openssl rand -base64 32)"     # Key vorher merken, er wird nicht gespeichert
```

```json
"Authentication": { "ApiKeys": [ { "Name": "leitstand", "KeySha256": "<hash>", "Role": "reader" } ] }
```

Der Dienst bindet standardmäßig nur an `localhost` und spricht HTTP. Wird er von außen erreichbar gemacht, gehört TLS (Reverse Proxy) davor, sonst laufen die Keys im Klartext durchs Netz.

### OPC-UA-Verbindung

- `Machines[].UseSecurity: true` wählt den sichersten vom Server angebotenen Endpoint (Signieren und Verschlüsseln). Standard ist `false` (wie bisher), dabei schreibt der Dienst eine Warnung ins Log.
- `Opc:AutoAcceptUntrustedCertificates` ist standardmäßig `false`. Ein unbekanntes Serverzertifikat wird abgelehnt, mit Thumbprint geloggt und landet in `pki/rejected`. Nach Prüfung nach `pki/trusted/certs` verschieben.
- Die Anmeldung beim OPC-Server erfolgt weiterhin anonym.
- Die verschlüsselte Verbindung ist gegen keinen echten Server getestet. Vor dem Produktivbetrieb mit dem eigenen Server prüfen.

### MCP und KI-Agenten

- `/mcp` bietet nur lesende Tools und `retry_message`. Es gibt **kein** Tool, das Werte auf der Maschine schreibt.
- Werte und Variablennamen kommen von der Maschine und sind aus Sicht des Agenten nicht vertrauenswürdiger Text (Prompt Injection ist über Textvariablen denkbar). Agenten sollten Werte als Daten behandeln, nicht als Anweisungen.

## Troubleshooting

| Problem                                   | Lösung                                                                                         |
|-------------------------------------------|------------------------------------------------------------------------------------------------|
| `connected: false` in `/api/machines`     | Server nicht erreichbar. Endpoint prüfen. Der Client versucht es alle 10 s erneut (siehe Log). |
| Keine Nachrichten trotz Verbindung        | `NodeId` prüfen (Namespace-Index, Schreibweise). Es werden nur Wertänderungen gespeichert.     |
| Nachricht steht auf `Failed`              | Fehlertext in `error` der Nachricht, dann über `/retry` erneut einplanen.                      |
| Port belegt                               | `Urls` in `appsettings.json` ändern.                                                           |
| 401 bei jedem Aufruf                      | Header `X-Api-Key` fehlt oder der Key passt nicht zum konfigurierten Hash. Im Development: `dev-reader-key`. |
| Dienst startet nicht (Optionsfehler)      | `Authentication:ApiKeys` fehlt oder ist ungültig (Name, 64-stelliger Hash, Rolle). Meldung im Log lesen. |
| OPC-Verbindung scheitert mit `UseSecurity`| Serverzertifikat liegt in `pki/rejected`: prüfen und nach `pki/trusted/certs` verschieben.     |
