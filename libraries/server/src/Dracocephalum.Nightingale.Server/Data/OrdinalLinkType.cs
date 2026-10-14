namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// The virtual streams whose ordinals are links, as <see cref="OrdinalLink.Type"/> stores them:
/// by number, since the type leads the key and the index of a table that grows with the events. The numbers are stored: never change or reuse one, and give a new member the
/// next unused number; 0 is no type. A unit test pins them.
/// </summary>
[StoredAsNumber]
public enum OrdinalLinkType : byte
{
    /// <summary>A correlation: <c>$bc-</c> and its correlation id.</summary>
    Correlation = 1,

    /// <summary>The first event of every stream: <c>$streams</c>, with an empty key.</summary>
    Streams = 2,

    /// <summary>The first event of every stream of a category: <c>$category-</c> and the category.</summary>
    CategoryStreams = 3,
}
