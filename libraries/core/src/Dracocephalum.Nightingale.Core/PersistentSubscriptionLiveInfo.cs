namespace Dracocephalum.Nightingale;

/// <summary>What the instance running a group knows about it while its consumer is connected.</summary>
/// <param name="ConnectedAt">When the consumer connected.</param>
/// <param name="InFlightCount">How many events are delivered and neither acknowledged nor refused yet.</param>
/// <param name="AwaitingRetryCount">How many events are queued to be delivered again.</param>
/// <param name="ConsumerBufferSize">How many delivered, unacknowledged events the consumer asked to hold at once.</param>
public sealed record PersistentSubscriptionLiveInfo(DateTimeOffset ConnectedAt, int InFlightCount, int AwaitingRetryCount, int ConsumerBufferSize);
