using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Reads the virtual streams, and <c>$all</c> below the mark, from the events table through the
/// read-only mirror of it. The store's own query surface knows nothing of the persisted
/// <c>category</c> column, and its type column is only reachable through its event-type
/// registry, so the predicates are written here, as LINQ that the filtered indexes answer with a
/// seek: tenant, key, then position, or tenant, key, then ordinal on a store with ordinals. Rows
/// are hydrated into event records the way the store adapter hydrates the store's events, body
/// re-serialized through the shared options and the two reserved metadata keys appended last, so
/// an event reads the same through either path; an integration test holds the two paths to the
/// same bytes. The queries run on the in-memory provider in unit tests, which is where their
/// bounds and their paging are held; that the database seeks rather than scans is an integration
/// test's to say.
/// </summary>
/// <param name="contexts">Makes the mirror to read through: the one over the main connection, or the one over the read-only connection.</param>
/// <param name="tenantId">The tenant every read is scoped to.</param>
internal sealed class VirtualStreamReader(Func<EventsDbContext> contexts, string tenantId)
{
    private static readonly Expression<Func<EventRow, Raw>> Plain =
        e => new Raw(e.SeqId, e.Id, e.StreamId, e.Version, e.Data, e.Type, e.Timestamp, e.CorrelationId, e.CausationId, e.Headers, null);

    private static readonly Expression<Func<EventRow, Raw>> WithCategoryOrdinal =
        e => new Raw(e.SeqId, e.Id, e.StreamId, e.Version, e.Data, e.Type, e.Timestamp, e.CorrelationId, e.CausationId, e.Headers, e.CategoryOrdinal);

    private static readonly Expression<Func<EventRow, Raw>> WithTypeOrdinal =
        e => new Raw(e.SeqId, e.Id, e.StreamId, e.Version, e.Data, e.Type, e.Timestamp, e.CorrelationId, e.CausationId, e.Headers, e.TypeOrdinal);

    /// <summary>The bounds of a virtual stream at or below a head.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="head">The highest position to consider.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The first and last positions, or <see langword="null"/> when there are none.</returns>
    public async Task<StreamHead?> HeadAsync(VirtualStreamName stream, long head, CancellationToken cancellationToken)
    {
        // Two aggregates, each one row off its end of the index; asked for together under one
        // grouping they would read every row between.
        await using var context = OpenContext();
        var live = QueryLive(context, stream).Where(e => e.SeqId <= head);
        var first = await live.MinAsync(e => (long?)e.SeqId, cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return null;
        }

        var last = await live.MaxAsync(e => (long?)e.SeqId, cancellationToken).ConfigureAwait(false);
        return new StreamHead(first.Value, last ?? first.Value);
    }

    /// <summary>One page of a virtual stream; see the port for the bounds.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="head">The highest position a forwards page may hold.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events of the page.</returns>
    public async Task<IReadOnlyList<EventRecord>> ReadAsync(VirtualStreamName stream, Direction direction, long from, long head, int count, CancellationToken cancellationToken)
    {
        await using var context = OpenContext();
        return await PageAsync(FilterByPosition(QueryLive(context, stream), direction, from, head), count, Plain, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What this connection holds of one page by position, with the high-water mark as this
    /// connection sees it, read on one open connection so both come from the same server:
    /// forwards, the page's events at or below the lower of the mark and the head; backwards, the
    /// page when it begins at or below the mark and nothing otherwise. Everything at or below the
    /// mark is on the server that reports it, because the mark is written after the events it
    /// covers.
    /// </summary>
    /// <param name="stream">The virtual stream, or <see langword="null"/> for <c>$all</c>.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="head">The highest position a forwards page may hold.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mark and the events.</returns>
    public async Task<(long Mark, IReadOnlyList<EventRecord> Events)> ReadBelowMarkAsync(VirtualStreamName? stream, Direction direction, long from, long head, int count, CancellationToken cancellationToken)
    {
        await using var context = OpenContext();
        if (context.Database.IsRelational())
        {
            // Held open for both queries: a pooled connection handed back between them could come
            // back as one to another server, whose mark says nothing about this one's events.
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var mark = await context.ProgressionRows.AsNoTracking()
            .Where(row => row.Name == EventsDbContext.HighWaterMark)
            .Select(row => (long?)row.LastSeqId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;
        var bound = Math.Min(mark, head);
        if (from > (direction == Direction.Forwards ? bound : mark))
        {
            return (mark, []);
        }

        var events = await PageAsync(FilterByPosition(QueryLive(context, stream), direction, from, bound), count, Plain, cancellationToken).ConfigureAwait(false);
        return (mark, events);
    }

    /// <summary>How many live events of a virtual stream lie after a position, up to the head.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="after">The position already delivered, exclusive.</param>
    /// <param name="head">The highest position to count, inclusive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count.</returns>
    public async Task<long> CountAsync(VirtualStreamName stream, long after, long head, CancellationToken cancellationToken)
    {
        await using var context = OpenContext();
        return await QueryLive(context, stream).LongCountAsync(e => e.SeqId > after && e.SeqId <= head, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The bounds of a virtual stream under ordinal numbering: the lowest and highest ordinal
    /// assigned, archived rows included, because a hole is still a number in the sequence.
    /// </summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bounds, or <see langword="null"/> when nothing is numbered.</returns>
    public async Task<StreamHead?> OrdinalHeadAsync(VirtualStreamName stream, CancellationToken cancellationToken)
    {
        await using var context = OpenContext();
        var all = FilterByKey(context.EventRows.AsNoTracking().Where(e => e.TenantId == tenantId), stream);
        var ordinals = stream.Kind == VirtualStreamKind.Category
            ? all.Where(e => e.CategoryOrdinal != null).Select(e => e.CategoryOrdinal)
            : all.Where(e => e.TypeOrdinal != null).Select(e => e.TypeOrdinal);
        var first = await ordinals.MinAsync(cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return null;
        }

        var last = await ordinals.MaxAsync(cancellationToken).ConfigureAwait(false);
        return new StreamHead(first.Value, last ?? first.Value);
    }

    /// <summary>One page of a virtual stream by ordinal, live rows only, each record carrying its ordinal.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">The ordinal to begin at, inclusive, in the reading direction.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events of the page.</returns>
    public async Task<IReadOnlyList<EventRecord>> ReadByOrdinalAsync(VirtualStreamName stream, Direction direction, long from, int count, CancellationToken cancellationToken)
    {
        await using var context = OpenContext();
        var live = QueryLive(context, stream);
        var forwards = direction == Direction.Forwards;
        if (stream.Kind == VirtualStreamKind.Category)
        {
            var ordered = forwards
                ? live.Where(e => e.CategoryOrdinal >= from).OrderBy(e => e.CategoryOrdinal)
                : live.Where(e => e.CategoryOrdinal <= from).OrderByDescending(e => e.CategoryOrdinal);
            return await PageAsync(ordered, count, WithCategoryOrdinal, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var ordered = forwards
                ? live.Where(e => e.TypeOrdinal >= from).OrderBy(e => e.TypeOrdinal)
                : live.Where(e => e.TypeOrdinal <= from).OrderByDescending(e => e.TypeOrdinal);
            return await PageAsync(ordered, count, WithTypeOrdinal, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IQueryable<EventRow> FilterByPosition(IQueryable<EventRow> events, Direction direction, long from, long head) =>
        direction == Direction.Forwards
            ? events.Where(e => e.SeqId >= from && e.SeqId <= head).OrderBy(e => e.SeqId)
            : events.Where(e => e.SeqId <= from).OrderByDescending(e => e.SeqId);

    private static IQueryable<EventRow> FilterByKey(IQueryable<EventRow> events, VirtualStreamName stream)
    {
        var key = stream.Key;
        return stream.Kind == VirtualStreamKind.Category
            ? events.Where(e => e.Category == key)
            : events.Where(e => e.Type == key);
    }

    private static async Task<IReadOnlyList<EventRecord>> PageAsync(IQueryable<EventRow> ordered, int count, Expression<Func<EventRow, Raw>> columns, CancellationToken cancellationToken)
    {
        var rows = await ordered.Take(count).Select(columns).ToListAsync(cancellationToken).ConfigureAwait(false);
        var records = new EventRecord[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            records[i] = Hydrate(rows[i]);
        }

        return records;
    }

    private static EventRecord Hydrate(Raw row)
    {
        // The body is re-serialized through the shared options rather than copied from the column,
        // so it comes back as the store adapter returns it, whatever escaping the store wrote.
        using var body = JsonDocument.Parse(row.Data);
        var metadata = row.Headers is null ? [] : JsonNode.Parse(row.Headers) as JsonObject ?? [];
        if (row.CorrelationId is not null)
        {
            metadata["$correlationId"] = row.CorrelationId;
        }

        if (row.CausationId is not null)
        {
            metadata["$causationId"] = row.CausationId;
        }

        return new EventRecord(
            row.Id,
            row.StreamId,
            row.Version - 1,
            row.SeqId,
            row.Type,
            row.Timestamp,
            JsonSerializer.SerializeToUtf8Bytes(body.RootElement, NightingaleJson.Default),
            metadata,
            row.Ordinal);
    }

    private EventsDbContext OpenContext() => contexts();

    /// <summary>The live events of the tenant, of one virtual stream or of all.</summary>
    private IQueryable<EventRow> QueryLive(EventsDbContext context, VirtualStreamName? stream)
    {
        var live = context.EventRows.AsNoTracking().Where(e => e.TenantId == tenantId && !e.IsArchived);
        return stream is { } named ? FilterByKey(live, named) : live;
    }

    /// <summary>The columns a read selects: never the whole row, since a store without ordinals has no ordinal columns.</summary>
    private sealed record Raw(long SeqId, Guid Id, string StreamId, long Version, string Data, string Type, DateTimeOffset Timestamp, string? CorrelationId, string? CausationId, string? Headers, long? Ordinal);
}
