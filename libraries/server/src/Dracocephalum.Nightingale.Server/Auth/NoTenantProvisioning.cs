namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>A store that needs nothing done for a new tenant: the conjoined store, or a test host.</summary>
public sealed class NoTenantProvisioning : ITenantProvisioner
{
    /// <inheritdoc/>
    public Task ProvisionAsync(string storeTenantId, CancellationToken cancellationToken) => Task.CompletedTask;
}
