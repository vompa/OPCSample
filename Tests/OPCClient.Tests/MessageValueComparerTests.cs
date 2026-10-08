using System.Reactive.Linq;
using System.Reactive.Subjects;
using OPCClient.Opc;

namespace OPCClient.Tests;

public class MessageValueComparerTests
{
    private static readonly MessageValueComparer Comparer = MessageValueComparer.Instance;

    [Fact]
    public void Equal_scalars_are_equal() =>
        Assert.True(Comparer.Equals(("T", 21.5), ("T", 21.5)));

    [Fact]
    public void Different_values_or_names_are_different()
    {
        Assert.False(Comparer.Equals(("T", 21.5), ("T", 22.0)));
        Assert.False(Comparer.Equals(("T", 1), ("S", 1)));
    }

    [Fact]
    public void Arrays_with_same_content_are_equal_although_different_instances() =>
        Assert.True(Comparer.Equals(("Profil", new[] { 1, 2, 3 }), ("Profil", new[] { 1, 2, 3 })));

    [Fact]
    public void Arrays_with_different_content_are_different() =>
        Assert.False(Comparer.Equals(("Profil", new[] { 1, 2, 3 }), ("Profil", new[] { 1, 2, 4 })));

    [Fact]
    public void Null_values_are_handled()
    {
        Assert.True(Comparer.Equals(("T", null), ("T", null)));
        Assert.False(Comparer.Equals(("T", null), ("T", 0)));
    }

    [Fact]
    public void Equal_values_have_equal_hash_codes() =>
        Assert.Equal(
            Comparer.GetHashCode(("Profil", new[] { 1, 2, 3 })),
            Comparer.GetHashCode(("Profil", new[] { 1, 2, 3 })));

    [Fact]
    public void Rx_pipeline_suppresses_repeated_array_values()
    {
        // Regression: ohne den Comparer gälten gleiche Array-Inhalte als Änderung (Referenzvergleich)
        var subject = new Subject<OpcMessage>();
        var received = new List<OpcMessage>();
        using var _ = subject.DistinctUntilChanged(m => (m.Name, m.Value), Comparer).Subscribe(received.Add);

        var now = DateTime.UtcNow;
        subject.OnNext(new OpcMessage("M", "Profil", new[] { 1, 2 }, now));
        subject.OnNext(new OpcMessage("M", "Profil", new[] { 1, 2 }, now));
        subject.OnNext(new OpcMessage("M", "Profil", new[] { 1, 3 }, now));

        Assert.Equal(2, received.Count);
    }
}
