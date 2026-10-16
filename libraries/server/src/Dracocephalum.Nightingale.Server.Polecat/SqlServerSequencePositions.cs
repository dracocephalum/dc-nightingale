using Dracocephalum.Nightingale.Server.Polecat.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The events table's positions as the tails read them, over the main connection, a context per
/// call. The shared sequence is the whole table; a tenant's is its rows. With locking reads every
/// read takes the table under <c>READCOMMITTEDLOCK</c>, so it waits on the row of an append still
/// open even where the database reads past it, under read committed snapshot: the best-effort
/// strategy then never sees an open append as a gap, only a position that will never commit.
/// Without them the read is the database's own, and the transaction horizon judges what it
/// cannot see.
/// </summary>
/// <param name="events">Makes the mirror of the events table.</param>
/// <param name="lockingReads">Whether every read waits on an open row rather than reading past it.</param>
internal sealed class SqlServerSequencePositions(IDbContextFactory<EventsDbContext> events, bool lockingReads) : ISequencePositions
{
    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<SequenceKey, long>> ReadCeilingsAsync(IReadOnlyCollection<SequenceKey> sequences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        await using var context = await events.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ceilings = new Dictionary<SequenceKey, long>();
        if (sequences.Contains(SequenceKey.Shared))
        {
            var shared = await Rows(context).MaxAsync(row => (long?)row.SeqId, cancellationToken).ConfigureAwait(false);
            if (shared is { } ceiling)
            {
                ceilings[SequenceKey.Shared] = ceiling;
            }
        }

        var tenants = sequences.Where(key => key.TenantId is not null).Select(key => key.TenantId!).ToList();
        if (tenants.Count > 0)
        {
            var byTenant = await Rows(context)
                .Where(row => tenants.Contains(row.TenantId))
                .GroupBy(row => row.TenantId)
                .Select(group => new { Tenant = group.Key, Ceiling = group.Max(row => row.SeqId) })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var tenant in byTenant)
            {
                ceilings[new SequenceKey(tenant.Tenant)] = tenant.Ceiling;
            }
        }

        return ceilings;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<long>> ReadFollowingAsync(SequenceKey sequence, long after, int window, CancellationToken cancellationToken)
    {
        await using var context = await events.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = Rows(context).Where(row => row.SeqId > after);
        if (sequence.TenantId is { } tenantId)
        {
            rows = rows.Where(row => row.TenantId == tenantId);
        }

        return await rows
            .OrderBy(row => row.SeqId)
            .Select(row => row.SeqId)
            .Take(window)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The events table, read past an open row or waiting on it: the hint sits on the table itself, inside the query EF composes around it.</summary>
    private IQueryable<EventRow> Rows(EventsDbContext context) =>
        lockingReads
#pragma warning disable EF1003 // The one value is the configured schema's name, quoted as an identifier; a table hint has no parameterized form.
            ? context.EventRows.FromSqlRaw("SELECT * FROM " + Quote(context.Schema) + ".[pc_events] WITH (READCOMMITTEDLOCK)")
#pragma warning restore EF1003
            : context.EventRows;

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
