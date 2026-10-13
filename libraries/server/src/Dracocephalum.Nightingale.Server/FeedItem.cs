namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// One item of a feed from <see cref="EventSource"/>: an event, or, under a filter, the last
/// position of a stretch examined that held nothing for the feed. The second is what lets a
/// group's checkpoint advance over a quiet stretch without delivering anything, and it travels
/// through the feed in order with the events, so that whoever reads it knows every event before
/// it has been handed over already.
/// </summary>
/// <param name="Record">The event, or <see langword="null"/> for a stretch examined.</param>
/// <param name="ScannedThrough">The last position examined: the event's own, or the stretch's end.</param>
public readonly record struct FeedItem(EventRecord? Record, long ScannedThrough)
{
    /// <summary>An event.</summary>
    /// <param name="record">The event.</param>
    /// <returns>The item.</returns>
    public static FeedItem Of(EventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(record, record.Position);
    }

    /// <summary>A stretch examined that held nothing for the feed.</summary>
    /// <param name="through">The last position examined.</param>
    /// <returns>The item.</returns>
    public static FeedItem Scanned(long through) => new(null, through);
}
