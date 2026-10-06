namespace Dracocephalum.Nightingale.Server;

/// <summary>One consumer of a running group, as the group describes it.</summary>
/// <param name="ConnectedAt">When the consumer connected.</param>
/// <param name="Address">Where it connected from, or <see langword="null"/> when it is not known.</param>
/// <param name="BufferSize">How many delivered, unacknowledged events it holds at once.</param>
/// <param name="InFlightCount">How many events are delivered to it and neither acknowledged nor refused yet.</param>
public sealed record SubscriptionConsumerLive(DateTimeOffset ConnectedAt, string? Address, int BufferSize, int InFlightCount);
