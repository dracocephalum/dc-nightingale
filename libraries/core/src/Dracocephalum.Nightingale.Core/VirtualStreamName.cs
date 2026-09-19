namespace Dracocephalum.Nightingale;

/// <summary>
/// The name of a virtual stream, parsed: every event of one category, or of one event type, in position order. It is
/// a predicate over <c>$all</c>, not a stream of its own: its positions are global and sparse, and
/// its head is its own last event, never the global head.
/// </summary>
/// <param name="Kind">Which predicate.</param>
/// <param name="Key">The category or the event type.</param>
public readonly record struct VirtualStreamName(VirtualStreamKind Kind, string Key)
{
    /// <summary>Gets the stream name, <c>$ce-</c> or <c>$et-</c> followed by the key.</summary>
    public string Name => (Kind == VirtualStreamKind.Category ? StreamNames.CategoryPrefix : StreamNames.EventTypePrefix) + Key;
}
