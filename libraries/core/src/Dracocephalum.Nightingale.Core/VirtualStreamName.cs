namespace Dracocephalum.Nightingale;

/// <summary>
/// The name of a virtual stream, parsed: every event of one category, of one event type, or every
/// data event, in position order. It is a predicate over <c>$all</c>, not a stream of its own: its
/// positions are global and sparse, and its head is its own last event, never the global head.
/// </summary>
/// <param name="Kind">Which predicate.</param>
/// <param name="Key">The category or the event type; empty for the data events.</param>
public readonly record struct VirtualStreamName(VirtualStreamKind Kind, string Key)
{
    /// <summary>
    /// Gets the stream name: <c>$ce-</c> or <c>$et-</c> followed by the key, or <c>$de</c>. The
    /// metadata events, <c>$mde</c>, are named as the event-type stream they are, so that one name
    /// is followed and matched whichever of the two a caller used.
    /// </summary>
    public string Name => Kind switch
    {
        VirtualStreamKind.Category => StreamNames.CategoryPrefix + Key,
        VirtualStreamKind.EventType => StreamNames.EventTypePrefix + Key,
        _ => StreamNames.DataEvents,
    };

    /// <summary>Gets a value indicating whether the stream can be read by ordinal: a category or an event type, never the data events.</summary>
    public bool HasOrdinals => Kind != VirtualStreamKind.DataEvents;
}
