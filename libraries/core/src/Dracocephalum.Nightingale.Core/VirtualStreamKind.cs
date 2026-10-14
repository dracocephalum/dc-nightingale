namespace Dracocephalum.Nightingale;

/// <summary>The predicates a virtual stream can be.</summary>
public enum VirtualStreamKind
{
    /// <summary>Every event of every stream in the category the key names, by <see cref="StreamNames.CategoryOf"/>: <c>$ce-orders</c>.</summary>
    Category = 0,

    /// <summary>Every event whose type is the key: <c>$et-OrderPlaced</c>.</summary>
    EventType = 1,

    /// <summary>
    /// Every event whose type does not begin with <c>$</c>: <c>$de</c>, with no key. Read by
    /// position only; numbering it would write every event a second time.
    /// </summary>
    DataEvents = 2,

    /// <summary>Every event whose <c>$correlationId</c> is the key: <c>$bc-checkout-1</c>, every event of one workflow.</summary>
    Correlation = 3,

    /// <summary>The first event of every stream, so one event a stream, in the order the streams began: <c>$streams</c>, with no key.</summary>
    Streams = 4,

    /// <summary>The first event of every stream in the category the key names: <c>$category-orders</c>, the streams of a category.</summary>
    CategoryStreams = 5,
}
