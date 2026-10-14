namespace Dracocephalum.Nightingale.Server.Polecat.Data;

/// <summary>
/// A stream as its row in the store's streams table, with the columns a read of the stream needs:
/// whether it exists, how far it goes, and whether it was deleted. Read, never written: the store
/// moves the version as it appends.
/// </summary>
internal sealed class StreamRow
{
    /// <summary>Gets or sets the tenant.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the stream name.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the version of the stream's last event, one-based; 0 for a stream with none.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream was deleted.</summary>
    public bool IsArchived { get; set; }

    /// <summary>Gets or sets the version through which the store compacted the stream; 0 when it never did.</summary>
    public long CompactedVersion { get; set; }

    /// <summary>Gets or sets the tenant's partition key, on a store partitioned by tenant only; see <see cref="EventRow.PartitionKey"/>.</summary>
    public int PartitionKey { get; set; }
}
