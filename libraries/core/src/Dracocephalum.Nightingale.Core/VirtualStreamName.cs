namespace Dracocephalum.Nightingale;

/// <summary>
/// The name of a virtual stream, parsed: every event of one category, of one event type, of one
/// correlation, or every data event, or the first event of every stream or of a category's, in
/// position order. It is a predicate over <c>$all</c>, not a stream of its own: its positions are
/// global and sparse, and its head is its own last event, never the global head.
/// </summary>
/// <param name="Kind">Which predicate.</param>
/// <param name="Key">The category, the event type or the correlation id; empty for the data events and for <c>$streams</c>.</param>
public readonly record struct VirtualStreamName(VirtualStreamKind Kind, string Key)
{
    /// <summary>
    /// Gets the stream name: its prefix followed by the key, or <c>$de</c> or <c>$streams</c>. The
    /// metadata events, <c>$mde</c>, are named as the event-type stream they are, so that one name
    /// is followed and matched whichever of the two a caller used.
    /// </summary>
    public string Name => Kind switch
    {
        VirtualStreamKind.Category => StreamNames.CategoryPrefix + Key,
        VirtualStreamKind.EventType => StreamNames.EventTypePrefix + Key,
        VirtualStreamKind.Correlation => StreamNames.CorrelationPrefix + Key,
        VirtualStreamKind.Streams => StreamNames.Streams,
        VirtualStreamKind.CategoryStreams => StreamNames.CategoryStreamsPrefix + Key,
        _ => StreamNames.DataEvents,
    };

    /// <summary>Gets a value indicating whether the stream can be read by ordinal: every virtual stream the sequencer numbers, which is all of them but the data events.</summary>
    public bool HasOrdinals => Kind is not VirtualStreamKind.DataEvents;
}
