using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>The ordinal sequencer's progress rows as the gateway's own context reads them, one query for every sequence asked.</summary>
/// <param name="contexts">Makes the gateway's own context.</param>
internal sealed class OrdinalSequencingProgressReader(IDbContextFactory<NightingaleDbContext> contexts) : IOrdinalSequencingProgressReader
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<OrdinalSequencingProgress>> ReadAsync(IReadOnlyCollection<string?> tenantIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.OrdinalSequencingProgress.AsNoTracking()
            .Where(row => tenantIds.Contains(row.TenantId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
