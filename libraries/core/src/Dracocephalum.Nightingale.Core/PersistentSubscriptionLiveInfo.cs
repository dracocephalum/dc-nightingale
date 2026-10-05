namespace Dracocephalum.Nightingale;

/// <summary>
/// How a running group stands while its consumer is connected. Asked of the instance that runs
/// the group it is as of now; in a listing it is what the group last wrote to the store, which
/// it does every few seconds, and <see cref="AsOf"/> says when.
/// </summary>
/// <param name="ConnectedAt">When the consumer connected.</param>
/// <param name="InFlightCount">How many events are delivered and neither acknowledged nor refused yet.</param>
/// <param name="AwaitingRetryCount">How many events are queued to be delivered again.</param>
/// <param name="ConsumerBufferSize">How many delivered, unacknowledged events the consumer asked to hold at once.</param>
/// <param name="Checkpoint">The checkpoint as the running group has it, which may be ahead of the stored one; <see langword="null"/> when there is none yet.</param>
/// <param name="OldestInFlightAt">When the oldest event still in flight was delivered; <see langword="null"/> when nothing is in flight.</param>
/// <param name="ConsumerAddress">Where the consumer connected from, as the server saw it; <see langword="null"/> when it is not known.</param>
/// <param name="AsOf">When these numbers were taken.</param>
public sealed record PersistentSubscriptionLiveInfo(
    DateTimeOffset ConnectedAt,
    int InFlightCount,
    int AwaitingRetryCount,
    int ConsumerBufferSize,
    long? Checkpoint = null,
    DateTimeOffset? OldestInFlightAt = null,
    string? ConsumerAddress = null,
    DateTimeOffset AsOf = default);
