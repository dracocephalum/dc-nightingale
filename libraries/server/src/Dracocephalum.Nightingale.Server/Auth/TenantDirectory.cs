using System.Collections.Concurrent;

using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// The tenants, as this instance knows them: the store id each is mapped to, which never
/// changes, and whether each is disabled, which does. A tenant is read once on first use and
/// kept; the table is read again on a cadence, so a tenant disabled or enabled on another
/// instance is noticed within one interval, and a tenant disabled here or there has its running
/// groups stopped here. A read that fails keeps what was known and says so: a database that
/// cannot be reached for a moment must not disable every tenant on the instance.
/// </summary>
public sealed partial class TenantDirectory(IDbContextFactory<NightingaleDbContext> contexts, SubscriptionGroupRegistry registry, ILogger<TenantDirectory> logger)
{
    private readonly ConcurrentDictionary<Guid, TenantEntry> _tenants = new();

    /// <summary>A tenant as the directory holds it.</summary>
    /// <param name="Id">The tenant's id.</param>
    /// <param name="StoreTenantId">The id the store keeps on its rows.</param>
    /// <param name="IsDisabled">Whether the tenant is disabled.</param>
    public sealed record TenantEntry(Guid Id, string StoreTenantId, bool IsDisabled);

    /// <summary>Finds a tenant, from memory or, the first time, from the table.</summary>
    /// <param name="tenantId">The tenant's id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tenant, or <see langword="null"/> when there is none.</returns>
    public async ValueTask<TenantEntry?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_tenants.TryGetValue(tenantId, out var known))
        {
            return known;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(tenant => tenant.Id == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return _tenants.GetOrAdd(tenantId, new TenantEntry(row.Id, row.StoreTenantId, row.IsDisabled));
    }

    /// <summary>Reads every tenant again and applies what changed: a tenant now disabled has its running groups here stopped.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the table has been read and applied.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        List<Tenant> rows;
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            rows = await context.Tenants.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRefreshFailed(logger, exception);
            return;
        }

        foreach (var row in rows)
        {
            var entry = new TenantEntry(row.Id, row.StoreTenantId, row.IsDisabled);
            _tenants.TryGetValue(row.Id, out var before);
            _tenants[row.Id] = entry;
            if (entry.IsDisabled && before?.IsDisabled != true)
            {
                Disable(entry);
            }
        }
    }

    /// <summary>Applies a change made on this instance at once, so its own callers see it before the next refresh.</summary>
    /// <param name="tenant">The tenant as it now is.</param>
    public void Apply(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var entry = new TenantEntry(tenant.Id, tenant.StoreTenantId, tenant.IsDisabled);
        _tenants[tenant.Id] = entry;
        if (tenant.IsDisabled)
        {
            Disable(entry);
        }
    }

    private void Disable(TenantEntry tenant)
    {
        // The instance serves one tenant's data today, so every running group is the tenant's;
        // a store serving many tenants will stop the tenant's groups only.
        registry.StopAll(new TenantDisabledException(tenant.Id));
        LogDisabled(logger, tenant.Id);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the tenants failed; what was known is kept until the next refresh.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant {TenantId} is disabled; its consumers on this instance are disconnected.")]
    private static partial void LogDisabled(ILogger logger, Guid tenantId);
}
