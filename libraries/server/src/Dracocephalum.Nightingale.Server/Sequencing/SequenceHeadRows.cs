using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The sequence heads in the gateway's own table, a context per call. A write reads the rows,
/// raises each that is behind, stamps every one, and saves; the position is the row's concurrency
/// token, so when another instance raised a row in between, or created it first, the write is
/// taken again from what is there, and a head is never lowered whichever instance writes last.
/// </summary>
/// <param name="contexts">Makes the gateway's context.</param>
internal sealed class SequenceHeadRows(IDbContextFactory<NightingaleDbContext> contexts) : ISequenceHeadRows
{
    /// <summary>How many times a write is taken again after another instance wrote the same rows.</summary>
    private const int Attempts = 3;

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<SequenceKey, long>> ReadAsync(IReadOnlyCollection<SequenceKey> sequences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await Query(context, sequences).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(row => new SequenceKey(row.TenantId), row => row.Position);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(IReadOnlyDictionary<SequenceKey, long> heads, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heads);
        if (heads.Count == 0)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WriteOnceAsync(heads, checkedAt, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt < Attempts)
            {
                // Another instance raised a row since it was read, or created one first: read
                // again and raise what is still behind.
            }
        }
    }

    private static IQueryable<SequenceHead> Query(NightingaleDbContext context, IReadOnlyCollection<SequenceKey> sequences)
    {
        var tenants = sequences.Where(key => key.TenantId is not null).Select(key => key.TenantId!).ToList();
        var shared = sequences.Contains(SequenceKey.Shared);
        return context.SequenceHeads.Where(row => (row.TenantId != null && tenants.Contains(row.TenantId)) || (shared && row.TenantId == null));
    }

    private async Task WriteOnceAsync(IReadOnlyDictionary<SequenceKey, long> heads, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await Query(context, heads.Keys.ToList()).ToDictionaryAsync(row => new SequenceKey(row.TenantId), cancellationToken).ConfigureAwait(false);
        foreach (var (key, position) in heads)
        {
            if (rows.TryGetValue(key, out var row))
            {
                row.Position = Math.Max(row.Position, position);
                row.CheckedAt = checkedAt;
            }
            else
            {
                context.SequenceHeads.Add(new SequenceHead { Id = Guid.NewGuid(), TenantId = key.TenantId, Position = position, CheckedAt = checkedAt });
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
