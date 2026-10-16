using Dracocephalum.Nightingale.Server.Polecat.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Chooses how the tails keep their heads on SQL Server, once, when they start: the transaction
/// horizon when the login may read it, which needs <c>VIEW DATABASE STATE</c> on the journal, and
/// otherwise the best-effort strategy, with a warning that names the grant. Nothing is required:
/// the best-effort strategy is the store's own rule with every read waiting on an open append's row,
/// so falling back to it costs nothing the store did not already cost. A grant takes effect at
/// the next start; until then the strategy chosen stays, so the head behaves one way for the life
/// of the process.
/// </summary>
internal static partial class SqlServerHeadStrategy
{
    /// <summary>Chooses the strategy.</summary>
    /// <param name="horizon">The journal's transaction horizon.</param>
    /// <param name="events">Makes the mirror of the events table.</param>
    /// <param name="logger">Where the fallback is said.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The strategy.</returns>
    public static async ValueTask<HeadStrategy> SelectAsync(ITransactionHorizon horizon, IDbContextFactory<EventsDbContext> events, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(horizon);
        ArgumentNullException.ThrowIfNull(logger);
        var availability = await horizon.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (availability.IsAvailable)
        {
            return new HeadStrategy(HeadStrategyKind.TransactionHorizon, horizon, new SqlServerSequencePositions(events, lockingReads: false));
        }

        LogBestEffort(logger, StaleThresholdHorizon.DefaultThreshold, availability.Reason);
        return new HeadStrategy(HeadStrategyKind.BestEffort, new StaleThresholdHorizon(StaleThresholdHorizon.DefaultThreshold), new SqlServerSequencePositions(events, lockingReads: true));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sequence heads are kept by the best-effort strategy: reads wait on an open append's row, and a missing position no read waits on is given up after {Threshold}, where the transaction horizon would give it up as soon as nothing could still commit it. The transaction horizon is not readable: {Reason} The grant takes effect at the next start.")]
    private static partial void LogBestEffort(ILogger logger, TimeSpan threshold, string? reason);
}
