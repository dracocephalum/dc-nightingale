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
    private readonly ITransactionHorizon _horizon;
    private readonly TimeProvider _timeProvider;
    private readonly long _takenAt;

    private HorizonCheckpoint(ITransactionHorizon horizon, TimeProvider timeProvider, long ceiling, HorizonMarker marker, long takenAt)
    {
        _horizon = horizon;
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
    /// Takes a checkpoint at a ceiling the caller has just read. The marker is taken here, after
    /// the read, never before it: a marker older than the ceiling could pass while the transaction
    /// that took a position below the ceiling is still open.
    /// </summary>
    /// <param name="horizon">The horizon of the journal the ceiling was read from.</param>
    /// <param name="ceiling">The highest position seen.</param>
    /// <param name="timeProvider">The clock the minimum age runs on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The checkpoint.</returns>
    public static async ValueTask<HorizonCheckpoint> TakeAsync(ITransactionHorizon horizon, long ceiling, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(horizon);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var marker = await horizon.MarkAsync(cancellationToken).ConfigureAwait(false);
        return new HorizonCheckpoint(horizon, timeProvider, ceiling, marker, timeProvider.GetTimestamp());
    }

    /// <summary>Gets how long ago the checkpoint was taken.</summary>
    /// <returns>The age.</returns>
    public TimeSpan GetAge() => _timeProvider.GetElapsedTime(_takenAt);

    /// <summary>
    /// Judges whether every position up to the ceiling is final: not before the minimum age, and
    /// then by asking the horizon, once a call.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The judgement, with the transaction holding the horizon back when one does.</returns>
    public async ValueTask<CheckpointJudgement> JudgeAsync(CancellationToken cancellationToken)
    {
        if (GetAge() < _horizon.MinimumAge)
        {
            return CheckpointJudgement.TooYoung;
        }

        var holder = await _horizon.FindHolderAsync(Marker, cancellationToken).ConfigureAwait(false);
        return holder is null ? CheckpointJudgement.Final : CheckpointJudgement.HeldBy(holder);
    }
}
