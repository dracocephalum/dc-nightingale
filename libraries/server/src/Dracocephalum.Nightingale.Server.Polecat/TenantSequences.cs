using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>A sequence per tenant: every tenant the gateway keeps, each with its own tail, which the listing asks for and so has polled.</summary>
/// <param name="tails">The tails.</param>
/// <param name="contexts">Makes the gateway's own context, where the tenants are.</param>
internal sealed class TenantSequences(PolecatTenantTails tails, IDbContextFactory<NightingaleDbContext> contexts) : INumberedSequences
{
    /// <inheritdoc/>
    public long Advances => tails.Advances;

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<NumberedSequence>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = await context.Tenants.AsNoTracking().Select(tenant => tenant.StoreTenantId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Select(id => new NumberedSequence(id, tails.For(id))).ToList();
    }

    /// <inheritdoc/>
    public Task<long> WaitForAnyAdvanceAsync(long seen, CancellationToken cancellationToken) => tails.WaitForAnyAdvanceAsync(seen, cancellationToken);
}
