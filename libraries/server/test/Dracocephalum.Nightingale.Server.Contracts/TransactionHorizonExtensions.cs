using Dracocephalum.Nightingale.Server.Sequencing;

namespace Dracocephalum.Nightingale.Server.Contracts;

/// <summary>A horizon asked about one sequence, as a test asks it.</summary>
public static class TransactionHorizonExtensions
{
    /// <summary>Finds the transaction holding the shared sequence back from a marker.</summary>
    /// <param name="horizon">The horizon.</param>
    /// <param name="marker">The marker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The holder, or <see langword="null"/> when the horizon has passed the marker.</returns>
    public static ValueTask<HorizonHolder?> FindHolderAsync(this ITransactionHorizon horizon, HorizonMarker marker, CancellationToken cancellationToken) =>
        horizon.FindHolderAsync(SequenceKey.Shared, marker, cancellationToken);

    /// <summary>Finds the transaction holding a sequence back from a marker.</summary>
    /// <param name="horizon">The horizon.</param>
    /// <param name="sequence">The sequence.</param>
    /// <param name="marker">The marker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The holder, or <see langword="null"/> when the horizon has passed the marker.</returns>
    public static async ValueTask<HorizonHolder?> FindHolderAsync(this ITransactionHorizon horizon, SequenceKey sequence, HorizonMarker marker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(horizon);
        var holders = await horizon.FindHoldersAsync(new Dictionary<SequenceKey, HorizonMarker> { [sequence] = marker }, cancellationToken);
        return holders.GetValueOrDefault(sequence);
    }
}
