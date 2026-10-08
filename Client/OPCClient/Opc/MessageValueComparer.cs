using System.Collections;

namespace OPCClient.Opc;

/// <summary>
/// Vergleicht (Name, Wert) für <c>DistinctUntilChanged</c>. OPC-Werte können Arrays sein; die würden mit dem
/// Standardvergleich per Referenz verglichen, und jede Notification mit gleichem Inhalt gälte als "geändert".
/// </summary>
public sealed class MessageValueComparer : IEqualityComparer<(string Name, object? Value)>
{
    public static readonly MessageValueComparer Instance = new();

    private static readonly IEqualityComparer Structural = StructuralComparisons.StructuralEqualityComparer;

    public bool Equals((string Name, object? Value) x, (string Name, object? Value) y) =>
        string.Equals(x.Name, y.Name, StringComparison.Ordinal) && Structural.Equals(x.Value, y.Value);

    public int GetHashCode((string Name, object? Value) obj) =>
        HashCode.Combine(obj.Name, obj.Value is null ? 0 : Structural.GetHashCode(obj.Value));
}
