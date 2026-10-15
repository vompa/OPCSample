# OPCSimulator

OPC-UA-Simulationsserver für Demo und Tests: Der Client läuft damit ohne externen Server (z. B. Prosys). Zwei Maschinen liefern Temperatur, Drehzahl, Status und Stückzahl mit gelegentlichen Überhitzungsphasen, sodass Alarme ausgelöst werden.

## Starten

```
dotnet run --project Server/OPCSimulator
dotnet run --project Server/OPCSimulator -- --port 4841 --seed 42
```

Beenden mit Strg+C. Endpoint: `opc.tcp://localhost:4840` (ohne Security). Zusätzlich wird `Basic256Sha256` (SignAndEncrypt) angeboten; das selbstsignierte Anwendungszertifikat liegt in `pki/simulator/` (nicht im Git). Der Simulator akzeptiert Client-Zertifikate automatisch.

## Knoten

Eigener Namespace `urn:vompa:opc-simulator` mit dem Namespace-Index 2 (beim Start geprüft). Je Maschine ein Ordner unter `Objects`:

| NodeId | Typ | Beschreibung |
|---|---|---|
| `ns=2;s=Presse-1.Temperatur` | Double | °C, um 60 °C mit Sinuskurve und Rauschen, bei Überhitzung über 80 °C |
| `ns=2;s=Presse-1.Drehzahl` | Double | U/min, 1200 bis 1500 im Betrieb, sonst 0 |
| `ns=2;s=Presse-1.Status` | String | `Läuft`, `Rüsten`, `Störung`, `Aus` |
| `ns=2;s=Presse-1.Stueckzahl` | Int32 | steigt nur im Status `Läuft` |
| `ns=2;s=Drehbank-2.Temperatur` | Double | wie oben, Grundwert um 58 °C |
| `ns=2;s=Drehbank-2.Drehzahl` | Double | 1250 bis 1450 im Betrieb, sonst 0 |
| `ns=2;s=Drehbank-2.Status` | String | wie oben |
| `ns=2;s=Drehbank-2.Stueckzahl` | Int32 | wie oben |

Bei `Rüsten`, `Störung` und `Aus` steht die Drehzahl auf 0 und die Stückzahl bleibt stehen. Werte tragen `SourceTimestamp` (UTC) und StatusCode `Good`; Änderungen kommen per Subscription beim Client an.

## Konfiguration

`appsettings.json`, Abschnitt `Simulator`; die Kommandozeile hat Vorrang.

| Einstellung | Kommandozeile | Standard | Bedeutung |
|---|---|---|---|
| `Port` | `--port` | 4840 | TCP-Port des Servers |
| `UpdateIntervalMs` | `--interval` | 1000 | Aktualisierungstakt in ms |
| `Seed` | `--seed` | leer | Seed des Zufallsgenerators (gesetzt = deterministisch) |
| `OverheatProbability` | `--overheat` | 0.01 | Wahrscheinlichkeit pro Takt, dass bei laufender Maschine eine Überhitzung beginnt |
| `EnableSecurity` | | true | `Basic256Sha256` zusätzlich zu `None` anbieten |
| `PkiPath` | | `pki/simulator` | Ordner für Zertifikate |

## Im Test verwenden

```csharp
await using var simulator = await SimulatorHost.StartAsync(new SimulatorOptions { Port = freePort, Seed = 42 });
// simulator.EndpointUrl
```

Aufbau: `MachineSimulation` (reine Wertelogik), `SimulatorNodeManager` (Adressraum), `SimulatorServer`, `SimulatorHost` (Start im Prozess und Takt), `Program.cs` (Konsole).
