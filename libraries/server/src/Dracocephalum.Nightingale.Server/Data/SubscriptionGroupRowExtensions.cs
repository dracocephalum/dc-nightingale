namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>What the group store reads off its rows: a group's row as its definition and its live snapshot, a lease's as its holder.</summary>
internal static class SubscriptionGroupRowExtensions
{
    /// <summary>The group the row defines, under the names it was created with.</summary>
    public static SubscriptionGroupDefinition ToDefinition(this SubscriptionGroup row)
    {
        var settings = new GroupSettings(
            row.StartPosition is { } start ? StreamPosition.From(start) : StreamPosition.End,
            TimeSpan.FromMilliseconds(row.MessageTimeoutMs),
            row.MaxRetryCount,
            row.CheckpointUpperBound,
            TimeSpan.FromMilliseconds(row.CheckpointAfterMs),
            row.CheckpointLowerBound,
            row.BufferSize,
            row.MaxSubscriberCount,
            row.Numbering,
            row.ConsumerStrategy,
            FilterColumn.FromColumn(row.Filter));

        // The names are the row's own: a case-insensitive database finds the row under another
        // spelling, and the group still goes by the names it was created with.
        return new SubscriptionGroupDefinition(row.Stream, row.Name, settings, row.CheckpointPosition) { Id = row.Id, TenantId = row.TenantId };
    }

    /// <summary>
    /// The row's snapshot, while the group's lease is held. Without a holder the columns are
    /// what an instance that stopped without clearing them left behind, and say nothing.
    /// </summary>
    public static SubscriptionGroupLive? ToLive(this SubscriptionGroup row, LeaseHolder? holder) =>
        holder is null || row.LiveSnapshotAt is not { } asOf
            ? null
            : new SubscriptionGroupLive(
                row.LiveConnectedAt ?? asOf,
                row.LiveInFlightCount ?? 0,
                row.LiveAwaitingRetryCount ?? 0,
                row.LiveConsumerBufferSize ?? 0,
                row.LiveCheckpointPosition,
                row.LiveOldestInFlightAt,
                row.LiveConsumerAddress,
                asOf,
                row.LiveConsumerCount ?? 1);

    /// <summary>Who holds the lease, and where that instance is reached when it said.</summary>
    public static LeaseHolder ToHolder(this Lease lease) =>
        new(lease.Owner, lease.OwnerAddress is null ? null : new Uri(lease.OwnerAddress, UriKind.Absolute));
}
