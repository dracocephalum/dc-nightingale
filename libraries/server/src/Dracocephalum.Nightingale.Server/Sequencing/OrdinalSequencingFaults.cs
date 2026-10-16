using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The faults on the progress rows, in the gateway's own table, a context per call. Only the fault
/// columns are written, so a batch committing the row's position meanwhile is never undone; a
/// sequence never numbered yet gets its row, at position 0, as the sequencer's batch would make it.
/// </summary>
/// <param name="contexts">Makes the gateway's own context.</param>
internal sealed class OrdinalSequencingFaults(IDbContextFactory<NightingaleDbContext> contexts) : IOrdinalSequencingFaults
{
    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<string?>> ReadFaultedAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.OrdinalSequencingProgress.AsNoTracking()
            .Where(row => row.Fault != null)
            .Select(row => row.TenantId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string? tenantId, string? fault, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.OrdinalSequencingProgress.SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            if (fault is null)
            {
                return;
            }

            row = new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = tenantId, Position = 0 };
            context.OrdinalSequencingProgress.Add(row);
        }

        row.Fault = fault is null ? null : fault[..Math.Min(fault.Length, 1000)];
        row.FaultedAt = fault is null ? null : at;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
