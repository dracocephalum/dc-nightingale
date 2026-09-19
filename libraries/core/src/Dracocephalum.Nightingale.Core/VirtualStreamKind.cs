namespace Dracocephalum.Nightingale;

/// <summary>The two predicates a virtual stream can be.</summary>
public enum VirtualStreamKind
{
    /// <summary>Every event of every stream whose name starts with the key and a hyphen: <c>$ce-orders</c>.</summary>
    Category = 0,

    /// <summary>Every event whose type is the key: <c>$et-OrderPlaced</c>.</summary>
    EventType = 1,
}
