namespace Dracocephalum.Nightingale;

/// <summary>One consumer of a running group, as the instance that runs the group sees it.</summary>
/// <param name="ConnectedAt">When the consumer connected.</param>
/// <param name="Address">Where it connected from, as the server saw it; <see langword="null"/> when it is not known.</param>
/// <param name="BufferSize">How many delivered, unacknowledged events it asked to hold at once.</param>
/// <param name="InFlightCount">How many events are delivered to it and neither acknowledged nor refused yet.</param>
public sealed record PersistentSubscriptionConsumerInfo(DateTimeOffset ConnectedAt, string? Address, int BufferSize, int InFlightCount);
