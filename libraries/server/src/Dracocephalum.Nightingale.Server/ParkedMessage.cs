namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// A message a group could not process: parked after its retries or on the consumer's request,
/// kept as one row per group and event until a replay moves it to the group's outbox. It
/// carries every number the event has, so it can be addressed by whichever one the group
/// speaks: the global position, which is also its key, the revision within its stream, and its
/// ordinal when it was read under ordinal numbering.
/// </summary>
/// <param name="Stream">The group's stream.</param>
/// <param name="Group">The group.</param>
/// <param name="Position">The event's global position.</param>
/// <param name="Revision">The event's revision within its own stream.</param>
/// <param name="Ordinal">The event's ordinal within the group's virtual stream, when the group is numbered by ordinal.</param>
/// <param name="EventId">The event's id.</param>
/// <param name="Reason">Why it was parked: the consumer's reason, or the retry limit.</param>
/// <param name="Attempts">How many deliveries it had.</param>
/// <param name="ParkedAt">When it was parked.</param>
public sealed record ParkedMessage(string Stream, string Group, long Position, long Revision, long? Ordinal, Guid EventId, string Reason, int Attempts, DateTimeOffset ParkedAt);
