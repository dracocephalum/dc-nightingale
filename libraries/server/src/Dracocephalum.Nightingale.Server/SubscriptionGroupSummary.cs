namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// A group with what the store knows around it: when it was created, how many messages are
/// parked and how many wait on its outbox, and which instance holds its lease now.
/// </summary>
/// <param name="Definition">The group.</param>
/// <param name="CreatedAt">When the group was created.</param>
/// <param name="ParkedCount">How many messages are parked.</param>
/// <param name="OutboxCount">How many messages are on the outbox.</param>
/// <param name="Holder">The instance holding the group's lease, or <see langword="null"/> when none does.</param>
/// <param name="Live">How the running group stood when it last wrote it, or <see langword="null"/> when no instance holds its lease or it has written nothing.</param>
public sealed record SubscriptionGroupSummary(SubscriptionGroupDefinition Definition, DateTimeOffset CreatedAt, long ParkedCount, long OutboxCount, LeaseHolder? Holder, SubscriptionGroupLive? Live = null);
