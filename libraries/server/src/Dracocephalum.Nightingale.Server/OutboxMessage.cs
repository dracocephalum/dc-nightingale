namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// A message due to be delivered to a group again, on its outbox: a parked message put back by a
/// replay. It carries every number its event has, as a parked message does, and when it is due;
/// a delivery that fails again moves it back to the parked messages.
/// </summary>
/// <param name="Stream">The group's stream.</param>
/// <param name="Group">The group.</param>
/// <param name="Position">The event's global position.</param>
/// <param name="Revision">The event's revision within its own stream.</param>
/// <param name="Ordinal">The event's ordinal within the group's virtual stream, when the group is numbered by ordinal.</param>
/// <param name="EventId">The event's id.</param>
/// <param name="Reason">Why it was parked, kept so a second failure keeps the history.</param>
/// <param name="Attempts">How many deliveries it had before it was parked.</param>
/// <param name="DueAt">When it becomes deliverable; now, for a replay.</param>
public sealed record OutboxMessage(string Stream, string Group, long Position, long Revision, long? Ordinal, Guid EventId, string Reason, int Attempts, DateTimeOffset DueAt);
