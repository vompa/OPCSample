using System.Collections.Concurrent;

namespace OPCClient.Opc;

public sealed class MachineRegistry
{
    private readonly ConcurrentDictionary<string, OpcMachine> _machines = new();

    public void Add(OpcMachine machine) => _machines[machine.Name] = machine;
    public OpcMachine? Get(string name) => _machines.GetValueOrDefault(name);
    public IEnumerable<OpcMachine> All => _machines.Values;
}
