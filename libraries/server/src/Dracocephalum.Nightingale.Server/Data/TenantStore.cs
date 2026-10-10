using System.Globalization;

using Dracocephalum.Nightingale.Server.Auth;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// The tenants table, through the gateway's own context. A new tenant's store id is the next
/// number after the highest in use, as invariant decimal text, so the key the store repeats on
/// every row is a few bytes; two instances creating at once are told apart by the unique index,
/// and the loser tries the next number.
/// </summary>
/// <param name="contexts">Makes a context per call.</param>
/// <param name="provisioner">Readies the store for a new tenant.</param>
/// <param name="directory">Where a change made here is applied at once.</param>
/// <param name="time">The clock.</param>
public sealed class TenantStore(IDbContextFactory<NightingaleDbContext> contexts, ITenantProvisioner provisioner, TenantDirectory directory, TimeProvider time) : ITenantStore
{
    /// <inheritdoc/>
    public async Task<Tenant> CreateAsync(string name, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (await context.Tenants.AnyAsync(row => row.Name == name, cancellationToken).ConfigureAwait(false))
            {
                throw new TenantExistsException($"A tenant named '{name}' exists.");
            }

            var taken = await context.Tenants.Select(row => row.StoreTenantId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var next = taken.Select(id => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0).DefaultIfEmpty(0).Max() + 1;
            var now = time.GetUtcNow();
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = name,
                StoreTenantId = next.ToString(CultureInfo.InvariantCulture),
                CreatedAt = now,
                UpdatedAt = now,
            };
            await provisioner.ProvisionAsync(tenant.StoreTenantId, cancellationToken).ConfigureAwait(false);
            context.Tenants.Add(tenant);
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                // The name or the number was taken under this write; the name is checked again
                // and the number moves on.
                continue;
            }

            directory.Apply(tenant);
            return tenant;
        }
    }

    /// <inheritdoc/>
    public async Task<Tenant?> UpdateAsync(Guid id, string? name, bool? disabled, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tenant = await context.Tenants.SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return null;
        }

        if (name is not null && name != tenant.Name)
        {
            if (await context.Tenants.AnyAsync(row => row.Name == name && row.Id != id, cancellationToken).ConfigureAwait(false))
            {
                throw new TenantExistsException($"A tenant named '{name}' exists.");
            }

            tenant.Name = name;
        }

        if (disabled is { } flag)
        {
            tenant.IsDisabled = flag;
        }

        tenant.UpdatedAt = time.GetUtcNow();
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            throw new TenantExistsException($"A tenant named '{name}' exists.", exception);
        }

        directory.Apply(tenant);
        return tenant;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Tenants.AsNoTracking().OrderBy(row => row.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
