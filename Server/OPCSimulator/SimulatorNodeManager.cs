using Opc.Ua;
using Opc.Ua.Server;

namespace OPCSimulator;

/// <summary>
/// Adressraum des Simulators: je Maschine ein Ordner unter Objects mit den Variablen
/// Temperatur, Drehzahl, Status und Stueckzahl (NodeId "ns=2;s=&lt;Maschine&gt;.&lt;Variable&gt;").
/// </summary>
public sealed class SimulatorNodeManager : CustomNodeManager2
{
    private readonly IReadOnlyList<MachineSimulation> _machines;
    private readonly Dictionary<string, MachineVariables> _variables = new();

    public SimulatorNodeManager(
        IServerInternal server, ApplicationConfiguration configuration, IReadOnlyList<MachineSimulation> machines)
        : base(server, configuration, SimulatorOptions.NamespaceUri)
    {
        _machines = machines;
        SystemContext.NodeIdFactory = this;
    }

    /// <summary>Index des Simulator-Namespaces im Server (soll 2 sein).</summary>
    public ushort SimulatorNamespaceIndex => NamespaceIndexes[0];

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var objectsReferences))
                externalReferences[ObjectIds.ObjectsFolder] = objectsReferences = new List<IReference>();

            foreach (var machine in _machines)
            {
                var folder = CreateFolder(machine.Name);
                folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                objectsReferences.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
                AddPredefinedNode(SystemContext, folder);

                var snapshot = machine.Snapshot;
                _variables[machine.Name] = new MachineVariables(
                    CreateVariable(folder, machine.Name, "Temperatur", DataTypeIds.Double, snapshot.Temperature),
                    CreateVariable(folder, machine.Name, "Drehzahl", DataTypeIds.Double, snapshot.Speed),
                    CreateVariable(folder, machine.Name, "Status", DataTypeIds.String, snapshot.Status),
                    CreateVariable(folder, machine.Name, "Stueckzahl", DataTypeIds.Int32, snapshot.PieceCount));
            }
        }
    }

    /// <summary>Übernimmt die aktuellen Werte aller Maschinen und meldet Änderungen an die Subscriptions.</summary>
    public void Refresh()
    {
        var now = DateTime.UtcNow;
        lock (Lock)
        {
            foreach (var machine in _machines)
            {
                if (!_variables.TryGetValue(machine.Name, out var variables)) continue;

                var snapshot = machine.Snapshot;
                Update(variables.Temperature, snapshot.Temperature, now);
                Update(variables.Speed, snapshot.Speed, now);
                Update(variables.Status, snapshot.Status, now);
                Update(variables.PieceCount, snapshot.PieceCount, now);
            }
        }
    }

    private void Update(BaseDataVariableState variable, object value, DateTime timestamp)
    {
        variable.Value = value;
        variable.StatusCode = StatusCodes.Good;
        variable.Timestamp = timestamp;
        variable.ClearChangeMasks(SystemContext, false);
    }

    private FolderState CreateFolder(string name) => new(null)
    {
        SymbolicName = name,
        ReferenceTypeId = ReferenceTypes.Organizes,
        TypeDefinitionId = ObjectTypeIds.FolderType,
        NodeId = new NodeId(name, NamespaceIndex),
        BrowseName = new QualifiedName(name, NamespaceIndex),
        DisplayName = new LocalizedText("de", name),
        WriteMask = AttributeWriteMask.None,
        UserWriteMask = AttributeWriteMask.None,
        EventNotifier = EventNotifiers.None
    };

    private BaseDataVariableState CreateVariable(
        NodeState parent, string machine, string name, NodeId dataType, object initialValue)
    {
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId($"{machine}.{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText("de", name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Historizing = false,
            Value = initialValue,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };
        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);
        return variable;
    }

    private sealed record MachineVariables(
        BaseDataVariableState Temperature, BaseDataVariableState Speed,
        BaseDataVariableState Status, BaseDataVariableState PieceCount);
}
