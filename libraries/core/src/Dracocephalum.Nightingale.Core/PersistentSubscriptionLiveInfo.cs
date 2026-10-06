namespace Dracocephalum.Nightingale;

/// <summary>
/// How a running group stands while consumers are connected. From the instance that runs the
/// group it is as of now; from any other it is what the group last wrote to the store, which it
/// does every few seconds. <see cref="FromOwner"/> says which, and <see cref="AsOf"/> when.
/// </summary>
/// <param name="ConnectedAt">When the first of the consumers connected.</param>
/// <param name="InFlightCount">How many events are delivered and neither acknowledged nor refused yet, over every consumer.</param>
/// <param name="AwaitingRetryCount">How many events are queued to be delivered again.</param>
/// <param name="ConsumerBufferSize">How many delivered, unacknowledged events the consumers asked to hold at once, over all of them.</param>
/// <param name="Checkpoint">The checkpoint as the running group has it, which may be ahead of the stored one; <see langword="null"/> when there is none yet.</param>
/// <param name="OldestInFlightAt">When the oldest event still in flight was delivered; <see langword="null"/> when nothing is in flight.</param>
/// <param name="ConsumerAddress">Where the first consumer connected from, as the server saw it; <see langword="null"/> when it is not known.</param>
/// <param name="AsOf">When these numbers were taken.</param>
/// <param name="FromOwner">Whether the instance that answered runs the group and read these from it, so they are as of now, or they are what the group last wrote to the store.</param>
/// <param name="ConsumerCount">How many consumers are connected.</param>
/// <param name="Consumers">Each consumer, when <paramref name="FromOwner"/>; the stored numbers hold the count and the totals, not each one.</param>
public sealed record PersistentSubscriptionLiveInfo(
    DateTimeOffset ConnectedAt,
    int InFlightCount,
    int AwaitingRetryCount,
    int ConsumerBufferSize,
    long? Checkpoint = null,
    DateTimeOffset? OldestInFlightAt = null,
    string? ConsumerAddress = null,
    DateTimeOffset AsOf = default,
    bool FromOwner = false,
    int ConsumerCount = 1,
    IReadOnlyList<PersistentSubscriptionConsumerInfo>? Consumers = null)
{
    /// <summary>Gets each consumer, or none when the numbers are from the store.</summary>
    public IReadOnlyList<PersistentSubscriptionConsumerInfo> Consumers { get; init; } = Consumers ?? [];
}
