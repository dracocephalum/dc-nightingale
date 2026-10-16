namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The highest position seen at a look, and a marker of the journal's horizon taken after it.
/// Positions are taken in order, so every position up to the ceiling was taken before the marker,
/// by a transaction already writing; once the horizon has passed the marker, and the marker is
/// older than the horizon's minimum age, every one of them is final: a position seen is committed,
/// a missing one never will be. The tail moves its head to the ceiling then, and the sequencer,
/// which numbers up to the head, numbers the range in order. Nothing is kept per position: one
/// checkpoint covers the range.
/// </summary>
public sealed class HorizonCheckpoint
{
    private readonly TimeProvider _timeProvider;
    private readonly long _takenAt;

    private HorizonCheckpoint(TimeProvider timeProvider, long ceiling, HorizonMarker marker, long takenAt)
    {
        _timeProvider = timeProvider;
        _takenAt = takenAt;
        Ceiling = ceiling;
        Marker = marker;
    }

    /// <summary>Gets the highest position seen at the look.</summary>
    public long Ceiling { get; }

    /// <summary>Gets the marker of the horizon, taken after the ceiling was read.</summary>
    public HorizonMarker Marker { get; }

    /// <summary>
    /// Takes a checkpoint at each ceiling the caller has read, under one marker. The marker is
    /// taken here, after the reads, never before them: a marker older than a ceiling could pass
    /// while the transaction that took a position below it is still open.
    /// </summary>
    /// <param name="horizon">The horizon of the journal the ceilings were read from.</param>
    /// <param name="ceilings">The highest position seen of each sequence.</param>
    /// <param name="timeProvider">The clock the minimum age runs on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The checkpoint of each sequence.</returns>
    public static async ValueTask<IReadOnlyDictionary<SequenceKey, HorizonCheckpoint>> TakeAsync(ITransactionHorizon horizon, IReadOnlyDictionary<SequenceKey, long> ceilings, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(horizon);
        ArgumentNullException.ThrowIfNull(ceilings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var marker = await horizon.MarkAsync(cancellationToken).ConfigureAwait(false);
        var takenAt = timeProvider.GetTimestamp();
        return ceilings.ToDictionary(pair => pair.Key, pair => new HorizonCheckpoint(timeProvider, pair.Value, marker, takenAt));
    }

    /// <summary>
    /// Judges whether every position up to each checkpoint's ceiling is final: a checkpoint younger
    /// than the horizon's minimum age is not, and the rest are asked of the horizon together, in
    /// one call, which is not made when none is old enough.
    /// </summary>
    /// <param name="horizon">The horizon that issued the checkpoints' markers.</param>
    /// <param name="checkpoints">The checkpoint over each sequence's gap.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The judgement of each sequence, with the transaction holding it back when one does.</returns>
    public static async ValueTask<IReadOnlyDictionary<SequenceKey, CheckpointJudgement>> JudgeAsync(ITransactionHorizon horizon, IReadOnlyDictionary<SequenceKey, HorizonCheckpoint> checkpoints, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(horizon);
        ArgumentNullException.ThrowIfNull(checkpoints);
        var judgements = new Dictionary<SequenceKey, CheckpointJudgement>(checkpoints.Count);
        var markers = new Dictionary<SequenceKey, HorizonMarker>(checkpoints.Count);
        foreach (var (sequence, checkpoint) in checkpoints)
        {
            if (checkpoint.GetAge() < horizon.MinimumAge)
            {
                judgements[sequence] = CheckpointJudgement.TooYoung;
            }
            else
            {
                markers[sequence] = checkpoint.Marker;
            }
        }

        if (markers.Count == 0)
        {
            return judgements;
        }

        var holders = await horizon.FindHoldersAsync(markers, cancellationToken).ConfigureAwait(false);
        foreach (var sequence in markers.Keys)
        {
            judgements[sequence] = holders.TryGetValue(sequence, out var holder) ? CheckpointJudgement.HeldBy(holder) : CheckpointJudgement.Final;
        }

        return judgements;
    }

    /// <summary>Gets how long ago the checkpoint was taken.</summary>
    /// <returns>The age.</returns>
    public TimeSpan GetAge() => _timeProvider.GetElapsedTime(_takenAt);
}
