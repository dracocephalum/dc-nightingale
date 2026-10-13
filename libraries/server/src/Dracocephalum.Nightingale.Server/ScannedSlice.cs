namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// What one scan of a stretch of <c>$all</c> under a filter found: the events the filter kept, in
/// the reading direction, and the last position the scan examined, which is where the next scan
/// begins after and where a subscriber's checkpoint may stand even when nothing was kept.
/// </summary>
/// <param name="Events">The events kept, in the reading direction; as many as were asked for means the stretch may hold more.</param>
/// <param name="Through">The last position examined, inclusive: the last event's when as many were kept as asked for, the end of the stretch otherwise.</param>
public sealed record ScannedSlice(IReadOnlyList<EventRecord> Events, long Through);
