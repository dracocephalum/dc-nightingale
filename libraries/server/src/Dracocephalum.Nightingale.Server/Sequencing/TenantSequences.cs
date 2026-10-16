using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>A sequence per tenant: every tenant the gateway keeps, each with its own tail, which the listing asks for and so has followed; those followed in this process are the tenants whose tail was asked for.</summary>
/// <param name="tails">The tails.</param>
/// <param name="contexts">Makes the gateway's own context, where the tenants are.</param>
internal sealed class TenantSequences(SequenceTails tails, IDbContextFactory<NightingaleDbContext> contexts) : INumberedSequences
{
    /// <inheritdoc/>
    public long Advances => tails.Advances;

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<NumberedSequence>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = await context.Tenants.AsNoTracking().Select(tenant => tenant.StoreTenantId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Select(SequenceOf).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<NumberedSequence> ListFollowed() => tails.Followed.Where(key => key.TenantId is not null).Select(key => SequenceOf(key.TenantId!)).ToList();

    /// <inheritdoc/>
    public Task<long> WaitForAnyAdvanceAsync(long seen, CancellationToken cancellationToken) => tails.WaitForAnyAdvanceAsync(seen, cancellationToken);

    private NumberedSequence SequenceOf(string storeTenantId) => new(storeTenantId, tails.For(new SequenceKey(storeTenantId)), tails.NumberingOf(new SequenceKey(storeTenantId)));
}
