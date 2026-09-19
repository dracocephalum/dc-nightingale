using Dracocephalum.Nightingale;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// One page of a stream read: the head as it was when the page was read, and the events of the page
/// in the reading direction. Fewer events than asked for means the read reached the end.
/// </summary>
/// <param name="Head">The stream's bounds at the time of the read.</param>
/// <param name="Events">The events of this page, possibly empty.</param>
public sealed record StreamSlice(StreamHead Head, IReadOnlyList<EventRecord> Events);
