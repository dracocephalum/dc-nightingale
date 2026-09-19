namespace Dracocephalum.Nightingale;

/// <summary>
/// What the server keeps for a persistent-subscription group. The defaults are the reference
/// client's where it has them: a group starts at the end of its stream, an event is redelivered
/// after thirty seconds unacknowledged and parked after ten retries, and the checkpoint is
/// written every thousand acknowledgements or every two seconds once ten have accrued.
/// </summary>
/// <param name="Start">Where the group starts: <see cref="StreamPosition.Start"/>, <see cref="StreamPosition.End"/>, or a position, inclusive.</param>
/// <param name="MessageTimeout">How long a delivered event may stay unacknowledged before it is redelivered.</param>
/// <param name="MaxRetryCount">How many times an event is redelivered before it is parked.</param>
/// <param name="CheckpointUpperBound">Acknowledgements that force a checkpoint write.</param>
/// <param name="CheckpointAfter">Time after which a checkpoint is written, given the lower bound.</param>
/// <param name="CheckpointLowerBound">The fewest acknowledgements a timed checkpoint write needs.</param>
/// <param name="BufferSize">How many events the server reads ahead of the consumer.</param>
/// <param name="MaxSubscriberCount">How many consumers may connect at once; only one is served today.</param>
public sealed record GroupSettings(
    StreamPosition Start,
    TimeSpan MessageTimeout,
    int MaxRetryCount,
    int CheckpointUpperBound,
    TimeSpan CheckpointAfter,
    int CheckpointLowerBound,
    int BufferSize,
    int MaxSubscriberCount)
{
    /// <summary>The defaults.</summary>
    public static GroupSettings Default { get; } = new(
        StreamPosition.End,
        TimeSpan.FromSeconds(30),
        10,
        1000,
        TimeSpan.FromSeconds(2),
        10,
        500,
        1);

    /// <summary>Checks the settings are usable.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MessageTimeout, TimeSpan.Zero, nameof(MessageTimeout));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetryCount, nameof(MaxRetryCount));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CheckpointUpperBound, nameof(CheckpointUpperBound));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CheckpointAfter, TimeSpan.Zero, nameof(CheckpointAfter));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CheckpointLowerBound, nameof(CheckpointLowerBound));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BufferSize, nameof(BufferSize));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSubscriberCount, nameof(MaxSubscriberCount));
    }
}
