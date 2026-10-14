namespace Dracocephalum.Nightingale.Server.Polecat.Data;

/// <summary>
/// A tenant's row in the registry a store partitioned by tenant keeps: the partition key its rows
/// carry. Several tenants share a key when the store buckets them. Read, never written: the
/// store assigns a key when it provisions a tenant.
/// </summary>
internal sealed class TenantPartitionRow
{
    /// <summary>Gets or sets the tenant.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the partition key, the store's <c>ordinal</c>, from 1.</summary>
    public int PartitionKey { get; set; }
}
