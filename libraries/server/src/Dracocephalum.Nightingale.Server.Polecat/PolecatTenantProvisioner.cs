using Dracocephalum.Nightingale.Server.Auth;
using Polecat;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Readies the store for a new tenant: nothing while the events table is one, a partition of
/// its own when the store partitions by tenant, which the store registers and splits itself.
/// </summary>
/// <param name="store">The store.</param>
/// <param name="options">Whether the store partitions by tenant.</param>
internal sealed class PolecatTenantProvisioner(IDocumentStore store, NightingaleOptions options) : ITenantProvisioner
{
    /// <inheritdoc/>
    public Task ProvisionAsync(string storeTenantId, CancellationToken cancellationToken) =>
        options.Store.Partitioning == PartitioningMode.Tenant
            ? store.Advanced.AddPolecatManagedTenantsAsync(cancellationToken, storeTenantId)
            : Task.CompletedTask;
}
