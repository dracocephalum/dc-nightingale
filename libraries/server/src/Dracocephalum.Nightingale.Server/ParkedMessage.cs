namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// A message a group could not process: parked after its retries or on the consumer's request,
/// kept as one row per group and event so it can be replayed or skipped one at a time.
/// </summary>
/// <param name="Stream">The group's stream.</param>
/// <param name="Group">The group.</param>
/// <param name="Position">The event's position in the group's stream: a revision for a stream, a position otherwise.</param>
/// <param name="EventId">The event's id.</param>
/// <param name="Reason">Why it was parked: the consumer's reason, or the retry limit.</param>
/// <param name="Attempts">How many deliveries it had.</param>
/// <param name="ParkedAt">When it was parked.</param>
/// <param name="Replay">Whether it is marked to be delivered again.</param>
public sealed record ParkedMessage(string Stream, string Group, long Position, Guid EventId, string Reason, int Attempts, DateTimeOffset ParkedAt, bool Replay);
