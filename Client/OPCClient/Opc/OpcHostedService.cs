using OPCClient.Storage;
using Serilog;

namespace OPCClient.Opc;

public class NodeConfig
{
    public string Name { get; set; } = "";
    public string NodeId { get; set; } = "";
}

public class MachineConfig
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";

    /// <summary>true = sicherster vom Server angebotener Endpoint (Signieren + Verschlüsseln). Standard: false (abwärtskompatibel).</summary>
    public bool UseSecurity { get; set; }
    public List<NodeConfig> Nodes { get; set; } = new();
}

/// <summary>Startet beim Hochfahren des Webservers alle konfigurierten Maschinen.</summary>
public sealed class OpcHostedService(
    MachineRegistry registry, SqliteMessageStore store, IMessageHandler handler, IConfiguration config) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var machines = config.GetSection("Opc:Machines").Get<List<MachineConfig>>() ?? new();
        var timeout = TimeSpan.FromSeconds(config.GetValue("Opc:HandlerTimeoutSeconds", 5));
        var autoAcceptCertificates = config.GetValue("Opc:AutoAcceptUntrustedCertificates", false);

        foreach (var mc in machines)
        {
            try
            {
                // Die Maschine wird immer registriert; die OPC-Verbindung baut sie im Hintergrund auf
                var machine = new OpcMachine(mc.Name, store)
                {
                    HandlerTimeout = timeout,
                    OnMessage = handler.HandleAsync
                };
                await machine.StartAsync(
                    token => OpcSessionFactory.CreateAsync(
                        mc.Endpoint, $"OPCClient-{mc.Name}", token, mc.UseSecurity, autoAcceptCertificates),
                    mc.Nodes.Select(n => (n.Name, n.NodeId)).ToArray());
                registry.Add(machine);
            }
            catch (Exception ex)
            {
                Log.ForContext("Machine", mc.Name).Error(ex, "Start fehlgeschlagen ({Endpoint})", mc.Endpoint);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var m in registry.All) await m.DisposeAsync();
    }
}
