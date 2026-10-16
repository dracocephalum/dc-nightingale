using Dracocephalum.Nightingale.Server.Polecat.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Chooses how the tails keep their heads on SQL Server, once, when they start, the first the
/// login may read: the horizon of each store partition, which needs
/// <c>VIEW SERVER PERFORMANCE STATE</c> on the server; the transaction horizon of the whole
/// database, which needs that or <c>VIEW DATABASE STATE</c> on the journal; and otherwise the
/// best-effort strategy. A step down is said, naming the grant. Nothing is required: the
/// best-effort strategy is the store's own rule with every read waiting on an open append's row,
/// so falling back to it costs nothing the store did not already cost. A grant takes effect at
/// the next start; until then the strategy chosen stays, so the head behaves one way for the life
/// of the process.
/// </summary>
internal static partial class SqlServerHeadStrategy
{
    /// <summary>Chooses the strategy.</summary>
    /// <param name="partitions">The horizon of each store partition.</param>
    /// <param name="transactions">The journal's transaction horizon.</param>
    /// <param name="events">Makes the mirror of the events table.</param>
    /// <param name="logger">Where a step down is said.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The strategy.</returns>
    public static async ValueTask<HeadStrategy> SelectAsync(ITransactionHorizon partitions, ITransactionHorizon transactions, IDbContextFactory<EventsDbContext> events, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(logger);
        var byPartition = await partitions.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (byPartition.IsAvailable)
        {
            return new HeadStrategy(HeadStrategyKind.PartitionHorizon, partitions, new SqlServerSequencePositions(events, lockingReads: false));
        }

        var byDatabase = await transactions.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (byDatabase.IsAvailable)
        {
            LogTransactionHorizon(logger, byPartition.Reason);
            return new HeadStrategy(HeadStrategyKind.TransactionHorizon, transactions, new SqlServerSequencePositions(events, lockingReads: false));
        }

        LogBestEffort(logger, StaleThresholdHorizon.DefaultThreshold, byPartition.Reason, byDatabase.Reason);
        return new HeadStrategy(HeadStrategyKind.BestEffort, new StaleThresholdHorizon(StaleThresholdHorizon.DefaultThreshold), new SqlServerSequencePositions(events, lockingReads: true));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The sequence heads are kept by the transaction horizon of the whole database: a transaction open anywhere in it since before a gap holds every head. The horizon of each store partition is not readable: {Reason} The grant takes effect at the next start.")]
    private static partial void LogTransactionHorizon(ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sequence heads are kept by the best-effort strategy: reads wait on an open append's row, and a missing position no read waits on is given up after {Threshold}, where a horizon would give it up as soon as nothing could still commit it. No horizon is readable: {PartitionReason} {DatabaseReason} The grant takes effect at the next start.")]
    private static partial void LogBestEffort(ILogger logger, TimeSpan threshold, string? partitionReason, string? databaseReason);
}
