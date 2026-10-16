namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// The virtual streams with ordinals, every one of them a link, as <see cref="OrdinalLink.Type"/>
/// stores them: by number, since the type leads the key and the index of a table that grows with
/// the events, and in the order of how many links each makes, the densest first. The numbers are
/// stored: never change or reuse one, and give a new member the next unused number; 0 is no type.
/// A unit test pins them.
/// </summary>
[StoredAsNumber]
public enum OrdinalLinkType : byte
{
    /// <summary>Every event of a type: <c>$et-</c> and the type. Every event has one, written in the same batch as its others, so it is the witness that an event is numbered.</summary>
    EventType = 1,

    /// <summary>Every event of a category: <c>$ce-</c> and the category.</summary>
    Category = 2,

    /// <summary>A correlation: <c>$bc-</c> and its correlation id.</summary>
    Correlation = 3,

    /// <summary>The first event of every stream: <c>$streams</c>, with an empty key.</summary>
    Streams = 4,

    /// <summary>The first event of every stream of a category: <c>$category-</c> and the category.</summary>
    CategoryStreams = 5,
}
