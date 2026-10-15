using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace OPCSimulator;

/// <summary>
/// Startet den Simulator im Prozess (Konsolen-App und Tests): OPC-UA-Server plus Takt, der die Maschinenwerte erneuert.
/// </summary>
public sealed class SimulatorHost : IAsyncDisposable
{
    /// <summary>Erwarteter Index des Simulator-Namespaces; die NodeIds lauten "ns=2;s=...".</summary>
    public const ushort ExpectedNamespaceIndex = 2;

    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });

    private readonly ApplicationInstance _application;
    private readonly SimulatorServer _server;
    private readonly IReadOnlyList<MachineSimulation> _machines;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task _updateLoop = Task.CompletedTask;
    private int _disposed;

    private SimulatorHost(
        ApplicationInstance application, SimulatorServer server, IReadOnlyList<MachineSimulation> machines,
        SimulatorOptions options, ILogger logger)
    {
        _application = application;
        _server = server;
        _machines = machines;
        _interval = TimeSpan.FromMilliseconds(options.UpdateIntervalMs);
        _logger = logger;
        EndpointUrl = options.EndpointUrl;
    }

    public string EndpointUrl { get; }

    public static async Task<SimulatorHost> StartAsync(
        SimulatorOptions options, CancellationToken ct = default, ILogger? logger = null)
    {
        options.Validate();
        logger ??= NullLogger.Instance;

        var machines = CreateMachines(options);
        var configuration = await CreateConfigurationAsync(options, ct);
        var application = new ApplicationInstance(configuration, Telemetry);
        await application.CheckApplicationInstanceCertificatesAsync(true, null, ct);

        var server = new SimulatorServer(machines);
        await application.StartAsync(server);

        var host = new SimulatorHost(application, server, machines, options, logger);
        try
        {
            host.VerifyNamespaceIndex();
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        host._updateLoop = Task.Run(host.RunUpdateLoopAsync, CancellationToken.None);
        logger.LogInformation("OPC-UA-Simulator läuft auf {Endpoint} (Takt {Interval} ms, Security {Security})",
            host.EndpointUrl, options.UpdateIntervalMs, options.EnableSecurity ? "None + Basic256Sha256" : "None");
        return host;
    }

    private static List<MachineSimulation> CreateMachines(SimulatorOptions options) =>
        MachineProfile.Defaults
            .Select((profile, index) => new MachineSimulation(
                profile,
                options.Seed is { } seed ? new Random(seed + index) : new Random(),
                options.OverheatProbability))
            .ToList();

    private static async Task<ApplicationConfiguration> CreateConfigurationAsync(SimulatorOptions options, CancellationToken ct)
    {
        const string applicationName = "OPCSimulator";
        var pki = options.PkiPath;

        var policies = new ServerSecurityPolicyCollection
        {
            new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
        };
        if (options.EnableSecurity)
        {
            policies.Add(new ServerSecurityPolicy
            {
                SecurityMode = MessageSecurityMode.SignAndEncrypt,
                SecurityPolicyUri = SecurityPolicies.Basic256Sha256
            });
        }

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = applicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationUri = "urn:localhost:OPCSimulator",
            ProductUri = "urn:vompa:opc-simulator:product",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = $"CN={applicationName}, O=vompa, DC=localhost"
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "rejected") },
                // Simulator für Demo und Tests: Client-Zertifikate werden ohne Rückfrage akzeptiert
                AutoAcceptUntrustedCertificates = true
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { options.EndpointUrl },
                SecurityPolicies = policies,
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200
            }
        };

        await configuration.ValidateAsync(ApplicationType.Server, ct);
        configuration.CertificateValidator.CertificateValidation += AcceptUntrustedClientCertificate;
        return configuration;
    }

    private static void AcceptUntrustedClientCertificate(CertificateValidator sender, CertificateValidationEventArgs e)
    {
        if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted) e.Accept = true;
    }

    private void VerifyNamespaceIndex()
    {
        var index = _server.CurrentInstance.NamespaceUris.GetIndex(SimulatorOptions.NamespaceUri);
        if (index != ExpectedNamespaceIndex || _server.NodeManager?.SimulatorNamespaceIndex != ExpectedNamespaceIndex)
            throw new InvalidOperationException(
                $"Der Namespace '{SimulatorOptions.NamespaceUri}' hat den Index {index}, erwartet wird {ExpectedNamespaceIndex}.");
    }

    private async Task RunUpdateLoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                foreach (var machine in _machines) machine.Step(_interval.TotalSeconds);
                _server.NodeManager?.Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // normales Beenden
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aktualisierung der Maschinenwerte abgebrochen");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await _cts.CancelAsync();
        await _updateLoop;
        await _application.StopAsync();
        _server.Dispose();
        _cts.Dispose();
        _logger.LogInformation("OPC-UA-Simulator beendet");
    }
}
