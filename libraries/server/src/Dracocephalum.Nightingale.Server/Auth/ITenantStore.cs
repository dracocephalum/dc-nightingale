using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>The tenants table, for management: the directory reads it on its own.</summary>
public interface ITenantStore
{
    /// <summary>Creates a tenant under a name, with a store id of its own.</summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row.</returns>
    /// <exception cref="TenantExistsException">The name is taken.</exception>
    Task<Tenant> CreateAsync(string name, CancellationToken cancellationToken);

    /// <summary>Renames a tenant, or disables or enables it.</summary>
    /// <param name="id">The tenant's id.</param>
    /// <param name="name">The new name, or <see langword="null"/> to keep it.</param>
    /// <param name="disabled">The new flag, or <see langword="null"/> to keep it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row as it now is, or <see langword="null"/> when there is no such tenant.</returns>
    /// <exception cref="TenantExistsException">The new name is taken.</exception>
    Task<Tenant?> UpdateAsync(Guid id, string? name, bool? disabled, CancellationToken cancellationToken);

    /// <summary>Lists every tenant, by name.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken);
}
