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
/// <param name="tenantId">The tenant every read is scoped to, or <see langword="null"/> for all tenants, under the wildcard.</param>
internal sealed class VirtualStreamReader(Func<EventsDbContext> contexts, string? tenantId)
{
    /// <summary>The columns of a read by position or revision, hydrated by <see cref="PageAsync"/>; a plain stream's read takes them too.</summary>
    internal static readonly Expression<Func<EventRow, Raw>> Plain =
        e => new Raw(e.SeqId, e.Id, e.StreamId, e.Version, e.Data, e.Type, e.Timestamp, e.CorrelationId, e.CausationId, e.Headers, null, e.Bdata);

    private static readonly Expression<Func<EventRow, Raw>> WithCategoryOrdinal =
        e => new Raw(e.SeqId, e.Id, e.StreamId, e.Version, e.Data, e.Type, e.Timestamp, e.CorrelationId, e.CausationId, e.Headers, e.CategoryOrdinal, e.Bdata);

    private static readonly Expression<Func<EventRow, Raw>> WithTypeOrdinal =
        e => new Raw(e.SeqId, e.Id, e.StreamId, e.Version, e.Data, e.Type, e.Timestamp, e.CorrelationId, e.CausationId, e.Headers, e.TypeOrdinal, e.Bdata);

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

    /// <summary>One page of <c>$all</c> by position, with the same bounds as <see cref="ReadAsync"/>: every live event of the tenant, or of all tenants.</summary>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="head">The highest position a forwards page may hold.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events of the page.</returns>
    public async Task<IReadOnlyList<EventRecord>> ReadAllAsync(Direction direction, long from, long head, int count, CancellationToken cancellationToken)
    {
        await using var context = OpenContext();
        return await PageAsync(FilterByPosition(QueryLive(context, null), direction, from, head), count, Plain, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One scan of a stretch of <c>$all</c> under a filter; see the port for the bounds. A prefix
    /// filter is the database's predicate, through the indexes where a prefix is a whole category or
    /// type; a regular expression is matched in process over the names of the stretch, read first
    /// without their bodies, and then the bodies of what it kept are read.
    /// </summary>
    /// <param name="filter">What to keep.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="head">The highest position a forwards scan may examine.</param>
    /// <param name="window">The most positions the scan examines.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events kept and the last position examined.</returns>
    public async Task<ScannedSlice> ScanAllAsync(EventFilter filter, Direction direction, long from, long head, int window, int count, CancellationToken cancellationToken)
    {
        var forwards = direction == Direction.Forwards;
        var (low, high) = forwards ? (from, Math.Min(head, from + window - 1)) : (Math.Max(1, from - window + 1), from);
        count = Math.Min(count, window);
        await using var context = OpenContext();
        var stretch = QueryLive(context, null).Where(e => e.SeqId >= low && e.SeqId <= high);
        IReadOnlyList<EventRecord> events;
        if (filter.Regex is null)
        {
            var kept = stretch.Where(FilterPredicates.ToPredicate(filter));
            events = await PageAsync(forwards ? kept.OrderBy(e => e.SeqId) : kept.OrderByDescending(e => e.SeqId), count, Plain, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var ordered = forwards ? stretch.OrderBy(e => e.SeqId) : stretch.OrderByDescending(e => e.SeqId);
            var names = await ordered.Select(e => new { e.SeqId, e.StreamId, e.Type }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var positions = names.Where(name => filter.Matches(name.StreamId, name.Type)).Take(count).Select(name => name.SeqId).ToList();
            if (positions.Count == 0)
            {
                events = [];
            }
            else
            {
                var rows = context.EventRows.AsNoTracking().Where(e => positions.Contains(e.SeqId));
                events = await PageAsync(forwards ? rows.OrderBy(e => e.SeqId) : rows.OrderByDescending(e => e.SeqId), positions.Count, Plain, cancellationToken).ConfigureAwait(false);
            }
        }

        var through = events.Count == count ? events[^1].Position : forwards ? high : low;
        return new ScannedSlice(events, through);
    }

    /// <summary>Where the live events of the tenant at positions within a range are: stream, revision and type, in position order, without bodies.</summary>
    /// <param name="from">The first position, inclusive.</param>
    /// <param name="head">The highest position the page may hold.</param>
    /// <param name="count">The most pointers to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pointers of the page.</returns>
    public async Task<IReadOnlyList<EventPointer>> ReadPointersAsync(long from, long head, int count, CancellationToken cancellationToken)
    {
        await using var context = OpenContext();
        return await FilterByPosition(QueryLive(context, null), Direction.Forwards, from, head)
            .Take(count)
            .Select(e => new EventPointer(e.StreamId, e.Version - 1, e.SeqId, e.Type, e.CorrelationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
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
        RequireOrdinals(stream);
        await using var context = OpenContext();
        var all = FilterByKey(context.EventRows.AsNoTracking().Where(e => (tenantId == null || e.TenantId == tenantId)), stream);
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
        RequireOrdinals(stream);
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

    /// <summary>
    /// The events of one virtual stream: a seek on the category, the type or the correlation
    /// index; the first event of each stream, a seek on the stream-starts index or, by category,
    /// on the category-starts one, which hold one row a stream; or, for the data events, the type
    /// checked on the rows a read of <c>$all</c> reads anyway, at the cost of <c>$all</c>, a
    /// prefix with no wildcard in it, so the database compares the first character.
    /// </summary>
    private static IQueryable<EventRow> FilterByKey(IQueryable<EventRow> events, VirtualStreamName stream)
    {
        var key = stream.Key;
        return stream.Kind switch
        {
            VirtualStreamKind.Category => events.Where(e => e.Category == key),
            VirtualStreamKind.EventType => events.Where(e => e.Type == key),
            VirtualStreamKind.Correlation => events.Where(e => e.CorrelationId == key),
            VirtualStreamKind.Streams => events.Where(e => e.Version == 1),
            VirtualStreamKind.CategoryStreams => events.Where(e => e.Version == 1 && e.Category == key),
            _ => events.Where(e => !e.Type.StartsWith('$')),
        };
    }

    /// <summary>A virtual stream read by ordinal: a category or an event type; the data events have none.</summary>
    private static void RequireOrdinals(VirtualStreamName stream)
    {
        if (!stream.HasOrdinals)
        {
            throw new ArgumentException($"{stream.Name} is read by position only; it has no ordinals.", nameof(stream));
        }
    }

    /// <summary>The first rows of an ordered query, as event records.</summary>
    internal static async Task<IReadOnlyList<EventRecord>> PageAsync(IQueryable<EventRow> ordered, int count, Expression<Func<EventRow, Raw>> columns, CancellationToken cancellationToken)
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
            metadata[MetadataKeys.CorrelationId] = row.CorrelationId;
        }

        if (row.CausationId is not null)
        {
            metadata[MetadataKeys.CausationId] = row.CausationId;
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
            row.Ordinal,
            row.Bdata);
    }

    private EventsDbContext OpenContext() => contexts();

    /// <summary>The live events of the tenant, of one virtual stream or of all.</summary>
    private IQueryable<EventRow> QueryLive(EventsDbContext context, VirtualStreamName? stream)
    {
        var live = context.EventRows.AsNoTracking().Where(e => (tenantId == null || e.TenantId == tenantId) && !e.IsArchived);
        return stream is { } named ? FilterByKey(live, named) : live;
    }

    /// <summary>The columns a read selects: never the whole row, since a store without ordinals has no ordinal columns.</summary>
    internal sealed record Raw(long SeqId, Guid Id, string StreamId, long Version, string Data, string Type, DateTimeOffset Timestamp, string? CorrelationId, string? CausationId, string? Headers, long? Ordinal, byte[]? Bdata);
}
