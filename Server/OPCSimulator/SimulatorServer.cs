using Opc.Ua;
using Opc.Ua.Server;

namespace OPCSimulator;

/// <summary>OPC-UA-Server mit genau einem eigenen Node Manager.</summary>
public sealed class SimulatorServer : StandardServer
{
    private readonly IReadOnlyList<MachineSimulation> _machines;

    public SimulatorServer(IReadOnlyList<MachineSimulation> machines)
    {
        _machines = machines;
    }

    public SimulatorNodeManager? NodeManager { get; private set; }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new SimulatorNodeManager(server, configuration, _machines);
        return new MasterNodeManager(server, configuration, null, NodeManager);
    }

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "vompa",
        ProductName = "OPCSimulator",
        ProductUri = "urn:vompa:opc-simulator:product",
        SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
        BuildNumber = Utils.GetAssemblyBuildNumber(),
        BuildDate = Utils.GetAssemblyTimestamp()
    };
}
