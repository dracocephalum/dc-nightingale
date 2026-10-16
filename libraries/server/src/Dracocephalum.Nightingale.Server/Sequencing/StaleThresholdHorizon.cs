namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The horizon of the best-effort strategy: no evidence of open transactions, only a wait. It has
/// passed every marker, and its minimum age is the stale threshold, so a checkpoint over a gap is
/// final once it is that old: the store's own rule for an abandoned gap, judged over the ceiling the
/// gap was first seen under rather than over timestamps the events carry. The tails then need one
/// rule, the checkpoint's, for both strategies.
/// </summary>
/// <param name="threshold">How long a checkpoint waits before the positions up to its ceiling are given up.</param>
public sealed class StaleThresholdHorizon(TimeSpan threshold) : ITransactionHorizon
{
    /// <summary>The threshold the store itself gives a gap up after: 3 seconds.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(3);

    /// <inheritdoc/>
    public TimeSpan MinimumAge => threshold;

    /// <inheritdoc/>
    public ValueTask<HorizonAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(HorizonAvailability.Available);

    /// <summary>Marks nothing: the checkpoint's age is the whole rule.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A marker that carries no value.</returns>
    public ValueTask<HorizonMarker> MarkAsync(CancellationToken cancellationToken) => ValueTask.FromResult(default(HorizonMarker));

    /// <summary>Finds no holder: without evidence of open transactions, the wait is all there is.</summary>
    /// <param name="markers">The markers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>No holder.</returns>
    public ValueTask<IReadOnlyDictionary<SequenceKey, HorizonHolder>> FindHoldersAsync(IReadOnlyDictionary<SequenceKey, HorizonMarker> markers, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyDictionary<SequenceKey, HorizonHolder>>(new Dictionary<SequenceKey, HorizonHolder>());
}
