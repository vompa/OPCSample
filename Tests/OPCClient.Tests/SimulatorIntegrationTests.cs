using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Opc.Ua;
using OPCClient.Opc;
using OPCClient.Storage;
using OPCSimulator;

namespace OPCClient.Tests;

/// <summary>
/// Integrationstest Client gegen Simulator: der Simulator läuft im Prozess auf einem freien Port,
/// eine echte <see cref="OpcMachine"/> verbindet sich und schreibt die Wertänderungen in eine eigene SQLite-Datei.
/// </summary>
public sealed class SimulatorIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"opc-sim-{Guid.NewGuid():N}.db");
    private readonly string _pkiPath = Path.Combine(Path.GetTempPath(), $"opc-sim-pki-{Guid.NewGuid():N}");
    private SimulatorHost _simulator = null!;
    private SqliteMessageStore _store = null!;
    private OpcMachine? _machine;

    public async Task InitializeAsync()
    {
        _store = new SqliteMessageStore(_dbPath);
        _simulator = await SimulatorHost.StartAsync(new SimulatorOptions
        {
            Port = GetFreePort(),
            UpdateIntervalMs = 200,
            Seed = 42,
            OverheatProbability = 0,
            PkiPath = _pkiPath
        });
    }

    public async Task DisposeAsync()
    {
        if (_machine is not null) await _machine.DisposeAsync();
        await _simulator.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            File.Delete(file);
        if (Directory.Exists(_pkiPath)) Directory.Delete(_pkiPath, true);
    }

    [Fact]
    public async Task Client_receives_value_changes_of_all_variables_in_the_message_store()
    {
        using var cts = new CancellationTokenSource(Timeout);
        _machine = new OpcMachine("Presse-1", _store)
        {
            OnMessage = (_, _) => Task.CompletedTask,
            ConnectRetryInterval = TimeSpan.FromSeconds(1)
        };

        await _machine.StartAsync(
            ct => OpcSessionFactory.CreateAsync(_simulator.EndpointUrl, "SimulatorTestClient", ct),
            ("Temperatur", "ns=2;s=Presse-1.Temperatur"),
            ("Drehzahl", "ns=2;s=Presse-1.Drehzahl"),
            ("Status", "ns=2;s=Presse-1.Status"),
            ("Stueckzahl", "ns=2;s=Presse-1.Stueckzahl"));

        await WaitUntilAsync(() =>
        {
            var names = _store.Query(new MessageFilter(Machine: "Presse-1", Limit: 500)).Items;
            return new[] { "Temperatur", "Drehzahl", "Status", "Stueckzahl" }.All(n => names.Any(m => m.Name == n))
                && names.Count(m => m.Name == "Temperatur") >= 3;
        }, "Nachrichten aller vier Variablen und mehrere Temperaturänderungen", cts.Token);

        Assert.True(_machine.IsConnected);

        var messages = _store.Query(new MessageFilter(Machine: "Presse-1", Limit: 500)).Items;
        var temperatures = messages.Where(m => m.Name == "Temperatur").Select(m => m.Value!.Value.GetDouble());
        Assert.All(temperatures, t => Assert.InRange(t, 30, 100));
        Assert.Contains(messages, m => m.Name == "Status" && m.Value!.Value.GetString() == MachineStatus.Running);
    }

    [Fact]
    public async Task Simulator_offers_namespace_index_2_and_both_machines()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var session = await OpcSessionFactory.CreateAsync(_simulator.EndpointUrl, "SimulatorTestClient", cts.Token);

        Assert.Equal(2, session.NamespaceUris.GetIndex(SimulatorOptions.NamespaceUri));

        foreach (var machine in new[] { "Presse-1", "Drehbank-2" })
        {
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Source,
                [new ReadValueId { NodeId = NodeId.Parse($"ns=2;s={machine}.Status"), AttributeId = Attributes.Value }],
                cts.Token);
            var status = response.Results[0];
            Assert.True(StatusCode.IsGood(status.StatusCode));
            Assert.Contains((string)status.Value, new[] { "Läuft", "Rüsten", "Störung", "Aus" });
        }

        await session.CloseAsync(cts.Token);
    }

    [Fact]
    public async Task Simulator_accepts_encrypted_connection_with_Basic256Sha256()
    {
        using var cts = new CancellationTokenSource(Timeout);
        TrustSimulatorCertificate();
        using var session = await OpcSessionFactory.CreateAsync(
            _simulator.EndpointUrl, "SimulatorTestClient", cts.Token, useSecurity: true);

        Assert.Equal(SecurityPolicies.Basic256Sha256, session.Endpoint!.SecurityPolicyUri);
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, session.Endpoint.SecurityMode);

        await session.CloseAsync(cts.Token);
    }

    [Fact]
    public async Task Encrypted_connection_to_untrusted_server_succeeds_only_with_auto_accept()
    {
        using var cts = new CancellationTokenSource(Timeout);

        await Assert.ThrowsAsync<ServiceResultException>(() => OpcSessionFactory.CreateAsync(
            _simulator.EndpointUrl, "SimulatorTestClient", cts.Token, useSecurity: true));

        using var session = await OpcSessionFactory.CreateAsync(
            _simulator.EndpointUrl, "SimulatorTestClient", cts.Token, useSecurity: true, autoAcceptUntrustedCertificates: true);
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, session.Endpoint!.SecurityMode);

        await session.CloseAsync(cts.Token);
    }

    /// <summary>Legt das Simulator-Zertifikat im Trust-Ordner des Clients ab (Arbeitsverzeichnis, pki/trusted).</summary>
    private void TrustSimulatorCertificate()
    {
        var target = Path.Combine("pki", "trusted", "certs");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(Path.Combine(_pkiPath, "own", "certs"), "*.der"))
            File.Copy(file, Path.Combine(target, "OPCSimulator-test.der"), true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, CancellationToken ct)
    {
        try
        {
            while (!condition())
                await Task.Delay(100, ct);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Zeitüberschreitung beim Warten auf: {what}");
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
