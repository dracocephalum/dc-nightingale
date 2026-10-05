namespace Dracocephalum.Nightingale;

/// <summary>
/// A message a persistent-subscription group has parked, as the server lists it: which event,
/// why, and at which retry count. The event itself is read from its stream by its number.
/// </summary>
/// <param name="Number">The event's number in the group's numbering, the one a replay or a skip takes: a revision for a group over a stream, a position over <c>$all</c> or a virtual stream, an ordinal for a group created under ordinal numbering.</param>
/// <param name="EventId">The event's id.</param>
/// <param name="Reason">Why it was parked: the consumer's reason, or that its retries were spent.</param>
/// <param name="RetryCount">Its retry count when it was parked: how many times it had been delivered again. A replay delivers it with this count.</param>
/// <param name="ParkedAt">When it was parked.</param>
/// <param name="Position">The event's global position.</param>
/// <param name="Revision">The event's revision within its own stream.</param>
public sealed record ParkedMessageInfo(long Number, Guid EventId, string Reason, int RetryCount, DateTimeOffset ParkedAt, long Position, long Revision);
