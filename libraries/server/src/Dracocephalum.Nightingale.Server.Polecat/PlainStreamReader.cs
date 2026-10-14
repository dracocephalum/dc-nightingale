using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Reads a plain stream from the events table through the mirror of it, with the stream's
/// metadata applied as the reference applies it to a read of a stream. Two statements, as the
/// store's own fetch took: the first finds the stream's row and, beside it in the same statement,
/// the last event of its metadata stream, a <c>TOP 1</c> seek backwards on the store's unique
/// index of a stream's versions, so a stream whose metadata was never set pays a probe of a few
/// index pages and no round trip. The second reads the page, with the lower bound of
/// <c>$maxCount</c> and <c>$tb</c> as the start of its seek on that index and <c>$maxAge</c> as a
/// check of each row's timestamp against the database's clock, the clock that stamped it. Neither
/// is cached: every instance appends and sets metadata, so a cache would serve what a change has
/// hidden until it learned of the change.
/// <para>
/// A metadata stream is read as it is stored, its whole history: the reference reads only its
/// last event, under a <c>$maxCount</c> of one of its own, which is a variance. So is a read of
/// the stream as stored, which the router asks for: what an append brought goes to the followers
/// of the stream's category and event type too, which metadata does not concern.
/// </para>
/// </summary>
/// <param name="contexts">Makes the mirror to read through: the one over the main connection, or the one over the read-only connection.</param>
/// <param name="tenantId">The tenant every read is scoped to; a stream name is per tenant.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class PlainStreamReader(Func<EventsDbContext> contexts, string tenantId, ILogger logger)
{
    /// <summary>
    /// The most seconds a <c>$maxAge</c> is checked for: the database adds a whole number of
    /// seconds of the width of an <c>int</c>, some 68 years, and nothing in a store is older.
    /// </summary>
    private const double LongestCheckedAge = int.MaxValue;

    /// <summary>One page of the stream, with its metadata applied unless it is a metadata stream; see the port for the bounds.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction; <see langword="null"/> for the start forwards and the end backwards.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head and the events of the page, or <see langword="null"/> when the stream has no events.</returns>
    /// <exception cref="StreamDeletedException">The stream was deleted.</exception>
    public Task<StreamSlice?> ReadAsync(string stream, Direction direction, long? from, int count, CancellationToken cancellationToken) =>
        ReadAsync(stream, direction, from, count, applyMetadata: !StreamNames.TryParseMetadataStream(stream, out _), cancellationToken);

    /// <summary>One page of the stream forwards, as stored, whatever its metadata says.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="from">The first revision, inclusive.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head and the events of the page, or <see langword="null"/> when the stream has no events.</returns>
    /// <exception cref="StreamDeletedException">The stream was deleted.</exception>
    public Task<StreamSlice?> ReadAsStoredAsync(string stream, long from, int count, CancellationToken cancellationToken) =>
        ReadAsync(stream, Direction.Forwards, from, count, applyMetadata: false, cancellationToken);

    private async Task<StreamSlice?> ReadAsync(string stream, Direction direction, long? from, int count, bool applyMetadata, CancellationToken cancellationToken)
    {
        await using var context = contexts();
        var found = applyMetadata
            ? await FindWithMetadata(context, stream, StreamNames.ToMetadataStream(stream)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await Find(context, stream).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (found is null || found.Version == 0)
        {
            return null;
        }

        if (found.IsArchived)
        {
            throw new StreamDeletedException(stream);
        }

        var metadata = ParseMetadata(stream, found.Metadata);
        var last = found.Version - 1;
        if (ResetMark.Find(metadata) is { } continuesFrom)
        {
            // Reset: not found until an append reaches the mark; from then on, a truncation.
            if (continuesFrom > last)
            {
                return null;
            }

            metadata = metadata with { TruncateBefore = continuesFrom };
        }

        var head = new StreamHead(Math.Max(found.CompactedVersion, metadata.GetFirstRevision(last)), last);
        var rows = FilterByAge(QueryRows(context, stream), metadata.MaxAge);
        IOrderedQueryable<EventRow> ordered;
        if (direction == Direction.Forwards)
        {
            var start = Math.Max(from ?? 0, head.First);
            if (start > last)
            {
                return new StreamSlice(head, []);
            }

            ordered = rows.Where(e => e.Version >= start + 1 && e.Version <= last + 1).OrderBy(e => e.Version);
        }
        else
        {
            var end = Math.Min(from ?? last, last);
            if (end < head.First)
            {
                return new StreamSlice(head, []);
            }

            var first = head.First;
            ordered = rows.Where(e => e.Version >= first + 1 && e.Version <= end + 1).OrderByDescending(e => e.Version);
        }

        var events = await VirtualStreamReader.PageAsync(ordered, count, VirtualStreamReader.Plain, cancellationToken).ConfigureAwait(false);
        return new StreamSlice(head, events);
    }

    /// <summary>
    /// What a reset, a metadata set and a failed "no stream" append decide on: the stream's last
    /// revision and whether it is archived, and its metadata stream's last revision and metadata.
    /// Two statements on the main connection, since none of the three is a first-tier path; each
    /// decision is written under an expected revision of the metadata stream, so a change of
    /// metadata between the two is caught when written, and an append between them is ordered
    /// after the decision, as the reference's single writer could have ordered it.
    /// </summary>
    /// <param name="stream">The plain stream's name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state.</returns>
    public async Task<MetadataState> ReadMetadataStateAsync(string stream, CancellationToken cancellationToken)
    {
        await using var context = contexts();
        var row = await Find(context, stream).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var last = await QueryRows(context, StreamNames.ToMetadataStream(stream))
            .OrderByDescending(e => e.Version)
            .Select(e => new { e.Version, e.Data })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return new MetadataState(
            row is null ? -1 : row.Version - 1,
            row?.IsArchived ?? false,
            last is null ? -1 : last.Version - 1,
            last is null ? StreamMetadata.Empty : ParseMetadata(stream, last.Data));
    }

    /// <summary>The stream's row and, in the same statement, the body of the last event of its metadata stream.</summary>
    internal IQueryable<Found> FindWithMetadata(EventsDbContext context, string stream, string metastream) =>
        context.StreamRows
            .Where(s => s.TenantId == tenantId && s.Id == stream)
            .Select(s => new Found(
                s.Version,
                s.IsArchived,
                s.CompactedVersion,
                context.EventRows
                    .Where(e => e.TenantId == tenantId && e.StreamId == metastream && !e.IsArchived)
                    .OrderByDescending(e => e.Version)
                    .Select(e => e.Data)
                    .FirstOrDefault()));

    /// <summary>The page's rows: those of the stream, the database's clock deciding which are too old for a <c>$maxAge</c>.</summary>
    internal static IQueryable<EventRow> FilterByAge(IQueryable<EventRow> rows, TimeSpan? maxAge)
    {
        if (maxAge is not { TotalSeconds: <= LongestCheckedAge } age)
        {
            return rows;
        }

        // UtcNow is the database's, translated rather than sent: an event's timestamp is the
        // database's clock when it was appended, and every instance's own clock differs from it.
        // The ban on it is for clocks read in process, which this one never is.
        var seconds = -age.TotalSeconds;
#pragma warning disable RS0030
        return rows.Where(e => e.Timestamp >= DateTimeOffset.UtcNow.AddSeconds(seconds));
#pragma warning restore RS0030
    }

    /// <summary>The rows of one stream of the tenant.</summary>
    internal IQueryable<EventRow> QueryRows(EventsDbContext context, string stream) =>
        context.EventRows.AsNoTracking().Where(e => e.TenantId == tenantId && e.StreamId == stream && !e.IsArchived);

    private IQueryable<Found> Find(EventsDbContext context, string stream) =>
        context.StreamRows
            .Where(s => s.TenantId == tenantId && s.Id == stream)
            .Select(s => new Found(s.Version, s.IsArchived, s.CompactedVersion, null));

    /// <summary>
    /// The metadata in force: none when nothing was set, and none, as in the reference, when what
    /// was stored does not read as metadata. Only the metadata call writes a metadata stream and it
    /// checks what it writes, so the second is a store written some other way.
    /// </summary>
    private StreamMetadata ParseMetadata(string stream, string? body)
    {
        if (body is null)
        {
            return StreamMetadata.Empty;
        }

        try
        {
            return StreamMetadata.FromJson(System.Text.Encoding.UTF8.GetBytes(body));
        }
        catch (FormatException exception)
        {
            LogUnreadableMetadata(logger, exception, stream);
            return StreamMetadata.Empty;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The metadata of {Stream} does not read as stream metadata; the stream is read as if it had none.")]
    private static partial void LogUnreadableMetadata(ILogger logger, Exception exception, string stream);

    /// <summary>What the first statement finds: the stream's row, and the body of its metadata when that was asked for and set.</summary>
    internal sealed record Found(long Version, bool IsArchived, long CompactedVersion, string? Metadata);

    /// <summary>A stream and its metadata as <see cref="ReadMetadataStateAsync"/> found them.</summary>
    /// <param name="StreamLast">The stream's last revision; -1 when it has no events.</param>
    /// <param name="IsArchived">Whether the stream is archived.</param>
    /// <param name="MetadataRevision">The metadata stream's last revision; -1 when nothing was set.</param>
    /// <param name="Metadata">The metadata in force, as stored: a reset's truncation negative.</param>
    internal sealed record MetadataState(long StreamLast, bool IsArchived, long MetadataRevision, StreamMetadata Metadata)
    {
        /// <summary>Gets the revision a reset stream continues from, or <see langword="null"/> when the metadata holds no reset.</summary>
        public long? ContinuesFrom => ResetMark.Find(Metadata);

        /// <summary>Gets a value indicating whether the stream is reset and not yet back: no append has reached the mark.</summary>
        public bool IsReset => ContinuesFrom > StreamLast;
    }
}
