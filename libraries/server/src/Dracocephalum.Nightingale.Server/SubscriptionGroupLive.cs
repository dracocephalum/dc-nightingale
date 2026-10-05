namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// How a running group stands: what it says when asked through the registry of the instance that
/// runs it, and what it writes to its row each time it renews its lease, so that an instance
/// that does not run it can still say.
/// </summary>
/// <param name="ConnectedAt">When the consumer connected.</param>
/// <param name="InFlightCount">How many events are delivered and neither acknowledged nor refused yet.</param>
/// <param name="AwaitingRetryCount">How many events are queued to be delivered again.</param>
/// <param name="ConsumerBufferSize">How many delivered, unacknowledged events the consumer holds at once.</param>
/// <param name="Checkpoint">The checkpoint as the running group has it, or <see langword="null"/> when there is none yet.</param>
/// <param name="OldestInFlightAt">When the oldest event still in flight was delivered, or <see langword="null"/> when nothing is.</param>
/// <param name="ConsumerAddress">Where the consumer connected from, or <see langword="null"/> when it is not known.</param>
/// <param name="AsOf">When these numbers were taken.</param>
public sealed record SubscriptionGroupLive(
    DateTimeOffset ConnectedAt,
    int InFlightCount,
    int AwaitingRetryCount,
    int ConsumerBufferSize,
    long? Checkpoint,
    DateTimeOffset? OldestInFlightAt,
    string? ConsumerAddress,
    DateTimeOffset AsOf);
