using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>The positions as the mirror of the events table reads them, over the main connection; a context per call.</summary>
/// <param name="events">Makes the mirror.</param>
internal sealed class EventRowPositions(IDbContextFactory<EventsDbContext> events) : ITenantEventPositions
{
    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, long>> ReadCeilingsAsync(IReadOnlyCollection<string> tenantIds, CancellationToken cancellationToken)
    {
        await using var context = await events.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.EventRows.AsNoTracking()
            .Where(row => tenantIds.Contains(row.TenantId))
            .GroupBy(row => row.TenantId)
            .Select(group => new { Tenant = group.Key, Ceiling = group.Max(row => row.SeqId) })
            .ToDictionaryAsync(ceiling => ceiling.Tenant, ceiling => ceiling.Ceiling, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<long>> ReadFollowingAsync(string tenantId, long mark, int window, CancellationToken cancellationToken)
    {
        await using var context = await events.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.EventRows.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.SeqId > mark)
            .OrderBy(row => row.SeqId)
            .Select(row => row.SeqId)
            .Take(window)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<long?> FindLastAppendedBeforeAsync(string tenantId, long mark, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var context = await events.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.EventRows.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.SeqId > mark && row.Timestamp <= cutoff)
            .MaxAsync(row => (long?)row.SeqId, cancellationToken)
            .ConfigureAwait(false);
    }
}
