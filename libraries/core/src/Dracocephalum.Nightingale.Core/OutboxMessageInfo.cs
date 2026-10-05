namespace Dracocephalum.Nightingale;

/// <summary>
/// A message on a persistent-subscription group's outbox, as the server lists it: a parked
/// message a replay put back, waiting to be delivered ahead of the stream.
/// </summary>
/// <param name="Number">The event's number in the group's numbering: a revision for a group over a stream, a position over <c>$all</c> or a virtual stream, an ordinal for a group created under ordinal numbering.</param>
/// <param name="EventId">The event's id.</param>
/// <param name="Reason">Why it was parked before it was put back.</param>
/// <param name="RetryCount">The retry count it is delivered with: how many times it had been delivered again before it was parked.</param>
/// <param name="DueAt">When it becomes deliverable.</param>
/// <param name="Position">The event's global position.</param>
/// <param name="Revision">The event's revision within its own stream.</param>
public sealed record OutboxMessageInfo(long Number, Guid EventId, string Reason, int RetryCount, DateTimeOffset DueAt, long Position, long Revision);
