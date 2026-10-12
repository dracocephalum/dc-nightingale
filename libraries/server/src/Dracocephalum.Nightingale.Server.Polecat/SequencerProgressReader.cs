using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>The sequencer's progress rows as the gateway's own context reads them, one query for every name asked.</summary>
/// <param name="contexts">Makes the gateway's own context.</param>
internal sealed class SequencerProgressReader(IDbContextFactory<NightingaleDbContext> contexts) : ISequencerProgressReader
{
    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, long>> ReadAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.SequencerProgress.AsNoTracking()
            .Where(row => names.Contains(row.Name))
            .ToDictionaryAsync(row => row.Name, row => row.Position, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
    }
}
