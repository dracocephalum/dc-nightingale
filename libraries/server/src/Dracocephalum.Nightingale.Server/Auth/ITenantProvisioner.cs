namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// What the store needs done when a tenant is created, before its first event: nothing in the
/// conjoined store, and a partition when the store partitions its events by tenant. The
/// backend implements it.
/// </summary>
public interface ITenantProvisioner
{
    /// <summary>Makes the store ready for a tenant.</summary>
    /// <param name="storeTenantId">The id the store will keep on the tenant's rows.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the store is ready.</returns>
    Task ProvisionAsync(string storeTenantId, CancellationToken cancellationToken);
}
