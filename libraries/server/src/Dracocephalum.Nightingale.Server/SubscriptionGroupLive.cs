namespace Dracocephalum.Nightingale.Server;

/// <summary>What a running group says about itself, asked through the registry of the instance that runs it.</summary>
/// <param name="ConnectedAt">When the consumer connected.</param>
/// <param name="InFlightCount">How many events are delivered and neither acknowledged nor refused yet.</param>
/// <param name="AwaitingRetryCount">How many events are queued to be delivered again.</param>
/// <param name="ConsumerBufferSize">How many delivered, unacknowledged events the consumer holds at once.</param>
public sealed record SubscriptionGroupLive(DateTimeOffset ConnectedAt, int InFlightCount, int AwaitingRetryCount, int ConsumerBufferSize);
