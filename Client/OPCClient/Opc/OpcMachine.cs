using System.Reactive.Linq;
using System.Reactive.Subjects;
using Opc.Ua;
using Opc.Ua.Client;
using OPCClient.Storage;
using Serilog;

namespace OPCClient.Opc;

/// <summary>
/// Eine Maschine = eine OPC-UA-Session + Rx-Stream + persistenter Stack (LIFO) + Worker.
/// </summary>
public sealed class OpcMachine : IAsyncDisposable
{
    private const int MaxAttempts = 3;

    private volatile Session? _session;
    private readonly SqliteMessageStore _store;
    private readonly Serilog.ILogger _log;
    private readonly Subject<OpcMessage> _subject = new();
    private readonly ISubject<OpcMessage> _stream;          // thread-sicher
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _pipeline;
    private Subscription? _subscription;
    private Task? _worker;
    private Task? _connector;
    private SessionReconnectHandler? _reconnect;
    private int _disposed;

    public string Name { get; }
    public bool IsConnected => _session?.Connected ?? false;

    /// <summary>Pause zwischen Verbindungsversuchen, solange der OPC-Server nicht erreichbar ist.</summary>
    public TimeSpan ConnectRetryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Handler für jede Nachricht (neueste zuerst). Das Token muss weitergereicht werden.</summary>
    public Func<OpcMessage, CancellationToken, Task>? OnMessage { get; set; }

    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Wartezeit vor dem nächsten Versuch (Parameter: Nummer des fehlgeschlagenen Versuchs).</summary>
    public Func<int, TimeSpan> RetryDelay { get; set; } = RetryBackoff.Next;

    public OpcMachine(string name, SqliteMessageStore store)
    {
        Name = name;
        _store = store;
        _log = Log.ForContext("Machine", name);
        _stream = Subject.Synchronize(_subject);
    }

    /// <summary>
    /// Startet sofort: Worker und DB-Backlog laufen unabhängig vom OPC-Server.
    /// Die Verbindung wird im Hintergrund (mit Wiederholung) aufgebaut.
    /// </summary>
    public Task StartAsync(Func<CancellationToken, Task<Session>> connect, params (string Name, string NodeId)[] nodes)
    {
        // Konfigurationsfehler (ungültige NodeId) sofort melden, statt endlos neu zu verbinden
        var parsed = nodes.Select(n => (n.Name, NodeId: ParseNodeId(n.Name, n.NodeId))).ToArray();

        // Rx: nur echte Änderungen, dann persistieren
        _pipeline = _stream
            .DistinctUntilChanged(m => (m.Name, m.Value), MessageValueComparer.Instance)
            .Subscribe(Push);

        // Nach Neustart: offene Nachrichten aus der DB wieder einplanen
        var pending = _store.PendingCount(Name);
        if (pending > 0)
        {
            _log.Information("{Pending} offene Nachrichten aus der DB wiederhergestellt", pending);
            _signal.Release(pending);
        }

        _worker = Task.Run(ProcessLoop);
        _connector = Task.Run(() => ConnectLoop(connect, parsed));
        _log.Information("Gestartet mit {Count} überwachten Variablen", nodes.Length);
        return Task.CompletedTask;
    }

    private static NodeId ParseNodeId(string name, string nodeId)
    {
        try { return NodeId.Parse(nodeId); }
        catch (Exception ex)
        {
            throw new ArgumentException($"Ungültige NodeId '{nodeId}' für Variable '{name}'", ex);
        }
    }

    private async Task ConnectLoop(Func<CancellationToken, Task<Session>> connect, (string Name, NodeId NodeId)[] nodes)
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            Session? session = null;
            try
            {
                session = await connect(ct);
                await AttachAsync(session, nodes);

                // Dispose kann während des Verbindens gestartet sein und hat die Session dann nicht gesehen
                if (ct.IsCancellationRequested)
                {
                    await DetachAsync(session);
                    return;
                }

                _log.Information("Mit OPC-Server verbunden");
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.Warning("Verbindungsaufbau fehlgeschlagen ({Reason}), neuer Versuch in {Seconds}s",
                    ex.Message, ConnectRetryInterval.TotalSeconds);
                await CloseQuietly(session);
            }
            catch (Exception)
            {
                await CloseQuietly(session);
                return;
            }

            try { await Task.Delay(ConnectRetryInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task DetachAsync(Session session)
    {
        session.KeepAlive -= OnKeepAlive;
        try
        {
            if (_subscription is not null) await _subscription.DeleteAsync(true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Fehler beim Entfernen der Subscription");
        }
        await CloseQuietly(session);
        _session = null;
    }

    private async Task AttachAsync(Session session, (string Name, NodeId NodeId)[] nodes)
    {
        var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = 1000 };

        foreach (var (name, nodeId) in nodes)
        {
            var item = new MonitoredItem(subscription.DefaultItem)
            {
                DisplayName = name,
                StartNodeId = nodeId,
                AttributeId = Attributes.Value,
                SamplingInterval = 500
            };
            item.Notification += (mi, _) =>
            {
                foreach (var v in mi.DequeueValues())
                {
                    var ts = v.SourceTimestamp == default ? DateTime.UtcNow : v.SourceTimestamp;
                    _stream.OnNext(new OpcMessage(Name, mi.DisplayName, v.Value, ts));
                }
            };
            subscription.AddItem(item);
        }

        session.AddSubscription(subscription);
        await subscription.CreateAsync();

        session.KeepAliveInterval = 5000;
        session.KeepAlive += OnKeepAlive;
        _subscription = subscription;
        _session = session;
    }

    private static async Task CloseQuietly(Session? session)
    {
        if (session is null) return;
        try { await session.CloseAsync(); } catch { /* beim Aufräumen egal */ }
    }

    /// <summary>Worker aufwecken, z. B. nach einem Requeue über die API.</summary>
    public void Wake() => _signal.Release();

    private void Push(OpcMessage msg)
    {
        try
        {
            var id = _store.Push(msg);
            _signal.Release();
            _log.Debug("Gespeichert #{Id} {Name}={Value}", id, msg.Name, msg.Value);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Speichern fehlgeschlagen: {Name}", msg.Name);
        }
    }

    private async Task ProcessLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cts.Token);

                var stored = _store.TryPop(Name);
                if (stored is null || OnMessage is null) continue;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeoutCts.CancelAfter(HandlerTimeout);

                try
                {
                    await OnMessage(stored.Message, timeoutCts.Token).WaitAsync(timeoutCts.Token);
                    _store.Complete(stored.Id);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // Shutdown: Nachricht bleibt offen und wird beim nächsten Start verarbeitet.
                    // Der abgebrochene Versuch zählt nicht (sonst endet mehrfaches Herunterfahren in Failed).
                    _store.Release(stored.Id);
                }
                catch (Exception ex)
                {
                    var timeout = ex is OperationCanceledException;
                    _log.Warning(ex, "Handler {Result} bei #{Id} {Name} (Versuch {Attempt})",
                        timeout ? "Timeout" : "Fehler", stored.Id, stored.Message.Name, stored.Attempts);

                    _store.Fail(stored.Id, stored.Attempts, timeout ? "Timeout" : ex.Message, MaxAttempts);

                    if (stored.Attempts < MaxAttempts) ScheduleRetry(stored.Attempts);
                    else _log.Error("Nachricht #{Id} endgültig fehlgeschlagen", stored.Id);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Weckt den Worker nach dem Backoff erneut. Der Worker blockiert dabei nicht: Nachrichten anderer Variablen
    /// werden in der Wartezeit weiter verarbeitet.
    /// </summary>
    private void ScheduleRetry(int failedAttempt)
    {
        var delay = RetryDelay(failedAttempt);
        _ = Task.Delay(delay, _cts.Token).ContinueWith(
            t => { if (t.IsCompletedSuccessfully) _signal.Release(); },
            TaskScheduler.Default);
    }

    private void OnKeepAlive(global::Opc.Ua.Client.ISession sender, KeepAliveEventArgs e)
    {
        if (!ServiceResult.IsBad(e.Status) || _reconnect is not null) return;

        _log.Warning("Verbindung verloren ({Status}), reconnect...", e.Status);
        _reconnect = new SessionReconnectHandler(OpcSessionFactory.Telemetry, false, -1);
        _reconnect.BeginReconnect(sender, 5000, (_, _) =>
        {
            if (_reconnect?.Session is Session s) _session = s;
            _reconnect?.Dispose();
            _reconnect = null;
            _log.Information("Wieder verbunden");
        });
    }

    public async ValueTask DisposeAsync()
    {
        // Mehrfaches Dispose ist laut IAsyncDisposable-Vertrag erlaubt
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _cts.Cancel();
        _pipeline?.Dispose();

        // Dispose darf nie ewig hängen, falls ein Handler das Token ignoriert
        var running = Task.WhenAll(new[] { _worker, _connector }.OfType<Task>());
        var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(3))) == running;

        var session = _session;
        if (session is not null) session.KeepAlive -= OnKeepAlive;
        _reconnect?.Dispose();

        try
        {
            if (_subscription is not null && session is not null) await _subscription.DeleteAsync(true);
            if (session is not null) await session.CloseAsync();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Fehler beim Schließen der Session");
        }

        // Läuft der Worker/Connector noch (hängender Handler, langsamer Connect), erst danach freigeben,
        // sonst greifen sie auf ein bereits disposed Token bzw. Subject zu
        if (finished) ReleaseResources();
        else _ = running.ContinueWith(_ => ReleaseResources(), TaskScheduler.Default);
    }

    private void ReleaseResources()
    {
        _subject.Dispose();
        _cts.Dispose();
    }
}
