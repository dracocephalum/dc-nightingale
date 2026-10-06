namespace Dracocephalum.Nightingale;

/// <summary>
/// How a persistent-subscription group shares its events among the consumers connected to it at
/// once. Whatever the strategy, an event is delivered to one consumer at a time, and one that is
/// refused or times out is delivered again, to whichever consumer the strategy then picks.
/// </summary>
public enum ConsumerStrategy
{
    /// <summary>
    /// Each event to the next consumer in turn, the first with room; the default, and the
    /// reference's. Two events of one stream can be with two consumers at once, so a consumer
    /// that folds a stream into state wants <see cref="Pinned"/>.
    /// </summary>
    RoundRobin = 0,

    /// <summary>
    /// A stream's events always to the same consumer, chosen by a hash of the stream name over
    /// the consumers connected, so a stream's events are processed in order by one consumer at a
    /// time. When a consumer joins or leaves, streams are shared out again over those that
    /// remain.
    /// </summary>
    Pinned = 1,

    /// <summary>
    /// Every event to the consumer that connected first; the others wait, and the next of them
    /// takes over when it leaves. The whole group's order is kept by one consumer at a time.
    /// </summary>
    DispatchToSingle = 2,
}
