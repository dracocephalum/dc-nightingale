using System.Text.Json;
using System.Text.RegularExpressions;

using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Polecat.Data;
using JasperFx.Events;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polecat;
using Polecat.Exceptions;
using Polecat.Linq;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The port implementation over the store. It does every conversion once, at this boundary: the
/// contract's zero-based revisions to the store's one-based versions, the named expected states to
/// the store's append variants, and the store's exceptions to the domain's. The store detects a
/// revision conflict itself, at commit, in the same transaction as the write; only the retry
/// comparison is done here, the reference way: on a conflict with an explicit expectation, the
/// events from that revision onward are compared with the batch by id, and an exact match means
/// the append already happened.
/// <para>
/// With a read-only connection, a page of <c>$all</c> or of a virtual stream by position is taken
/// from it as far as it reaches and from the main connection beyond, so the answer is the one the
/// main connection alone would give; a bounded read of a plain stream is taken from it only when
/// the host asks, and may then lag. Everything else uses the main connection.
/// </para>
/// </summary>
/// <param name="store">The store.</param>
/// <param name="tenantId">The tenant every session is opened for, or <see langword="null"/> for every tenant, under the wildcard: then <c>$all</c> and the virtual streams read across tenants and a stream name, which is per tenant, is refused.</param>
/// <param name="events">Makes the mirror of the store's events table over the main connection, for the virtual streams.</param>
/// <param name="ordinals">Whether the store was initialized with ordinals.</param>
/// <param name="readOnlyEvents">Makes the same mirror over the read-only connection, or <see langword="null"/> when the host does not read through it.</param>
/// <param name="readOnlyStore">A store over the read-only connection for bounded reads of plain streams, owned by whoever made this instance, or <see langword="null"/> when they stay on the main connection.</param>
/// <param name="contexts">Makes the gateway's own context, for the sequencer's progress.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class PolecatStreamStore(IDocumentStore store, string? tenantId, IDbContextFactory<EventsDbContext> events, bool ordinals, IDbContextFactory<ReadOnlyEventsDbContext>? readOnlyEvents, IDocumentStore? readOnlyStore, IDbContextFactory<NightingaleDbContext> contexts, TimeProvider timeProvider, ILogger<PolecatStreamStore> logger) : IStreamStore
{
    /// <summary>SQL Server's error for a value longer than its column, naming the column.</summary>
    /// <summary>
    /// The most events the store takes in one append: it writes an append as one command with
    /// eleven parameters an event and a few besides, and SQL Server takes 2100 in a command.
    /// Measured, and held by a test, which fails when a version of the store changes it.
    /// </summary>
    public const int MaxEventsPerAppend = 190;

    private const int TruncationWithColumn = 2628;

    /// <summary>The same error as servers that do not name the column raise it.</summary>
    private const int Truncation = 8152;

    /// <summary>SQL Server's error for a command with more parameters than it takes, 2100.</summary>
    private const int TooManyParameters = 8003;

    private readonly VirtualStreamReader _virtual = new(events.CreateDbContext, tenantId);
    private readonly VirtualStreamReader? _replica = readOnlyEvents is null ? null : new(readOnlyEvents.CreateDbContext, tenantId);
    private readonly ReplicaRouter _router = new(timeProvider, logger);

    /// <inheritdoc/>
    public bool OrdinalsEnabled => ordinals;

    /// <inheritdoc/>
    public async Task<AppendResult> AppendAsync(string stream, StreamState expected, IReadOnlyList<EventData> events, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfZero(events.Count);

        var wrapped = new object[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            wrapped[i] = JsonEvent.From(events[i]);
        }

        await using var session = store.LightweightSession(ToSessionOptions(RequireTenant(stream)));
        if (expected == StreamState.NoStream)
        {
            session.Events.StartStream(stream, wrapped);
        }
        else if (expected == StreamState.Any)
        {
            session.Events.Append(stream, wrapped);
        }
        else if (expected == StreamState.StreamExists)
        {
            var state = await session.Events.FetchStreamStateAsync(stream, cancellationToken).ConfigureAwait(false);
            if (state is null || state.Version == 0)
            {
                throw new RevisionConflictException(stream, expected, -1);
            }

            if (state.IsArchived)
            {
                throw new StreamDeletedException(stream);
            }

            session.Events.Append(stream, wrapped);
        }
        else
        {
            // The store's expected version is the version after the append, one-based.
            session.Events.Append(stream, expected.ToInt64() + 1 + wrapped.Length, wrapped);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            return await ResolveConflict(stream, expected, events, cancellationToken).ConfigureAwait(false);
        }
        catch (JasperFx.Events.ExistingStreamIdCollisionException)
        {
            return await ResolveConflict(stream, expected, events, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidStreamException)
        {
            throw new StreamDeletedException(stream);
        }
        catch (SqlException exception) when (exception.Number == TooManyParameters)
        {
            // The store writes an append as one command, a number of parameters per event, and
            // the database takes 2100 in one. The server's limit keeps appends under that; this
            // is for a store version that spends more parameters per event than the limit
            // allowed for, so that it is still an answer and never an unhandled failure.
            throw new AppendSizeExceededException(stream, 0, exception);
        }
        catch (SqlException exception) when (exception.Number is TruncationWithColumn or Truncation)
        {
            // The store's columns hold so many bytes of the encoded text, and the database is what
            // measures: it refuses the write, names the column, and nothing is stored cut short.
            throw new ValueTooLongException(DescribeColumn(exception), exception);
        }

        var last = (IEvent)wrapped[^1];
        return new AppendResult(last.Version - 1, last.Sequence);
    }

    /// <inheritdoc/>
    public Task<StreamSlice?> ReadAsync(string stream, Direction direction, long? from, int count, CancellationToken cancellationToken) =>
        ReadFromAsync(store, RequireTenant(stream), stream, direction, from, count, cancellationToken);

    /// <inheritdoc/>
    public Task<StreamSlice?> ReadEventualAsync(string stream, Direction direction, long? from, int count, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(stream);
        return readOnlyStore is null
            ? ReadFromAsync(store, tenant, stream, direction, from, count, cancellationToken)
            : _router.PreferReplicaAsync(
                token => ReadFromAsync(readOnlyStore, tenant, stream, direction, from, count, token),
                token => ReadFromAsync(store, tenant, stream, direction, from, count, token),
                cancellationToken);
    }

    private static async Task<StreamSlice?> ReadFromAsync(IDocumentStore reads, string tenant, string stream, Direction direction, long? from, int count, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        await using var session = reads.QuerySession(ToSessionOptions(tenant));
        var state = await session.Events.FetchStreamStateAsync(stream, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Version == 0)
        {
            return null;
        }

        if (state.IsArchived)
        {
            throw new StreamDeletedException(stream);
        }

        var head = new StreamHead(state.CompactedVersion, state.Version - 1);
        long lower;
        long upper;
        if (direction == Direction.Forwards)
        {
            var start = from ?? 0;
            if (start > head.Last)
            {
                return new StreamSlice(head, []);
            }

            lower = start + 1;
            upper = Math.Min(head.Last + 1, start + count);
        }
        else
        {
            var start = Math.Min(from ?? head.Last, head.Last);
            upper = start + 1;
            lower = Math.Max(1, upper - count + 1);
        }

        var stored = await session.Events.FetchStreamAsync(stream, version: upper, fromVersion: lower, token: cancellationToken).ConfigureAwait(false);
        var records = new EventRecord[stored.Count];
        for (var i = 0; i < stored.Count; i++)
        {
            var source = direction == Direction.Forwards ? stored[i] : stored[stored.Count - 1 - i];
            records[i] = ToRecord(source);
        }

        return new StreamSlice(head, records);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EventRecord>> ReadAllAsync(Direction direction, long from, long head, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return _replica is null
            ? ReadAllFromPrimaryAsync(direction, from, head, count, cancellationToken)
            : _router.ReadAsync(
                direction,
                from,
                head,
                count,
                token => _replica.ReadBelowMarkAsync(null, direction, from, head, count, token),
                (start, most, token) => ReadAllFromPrimaryAsync(direction, start, head, most, token),
                cancellationToken);
    }

    private async Task<IReadOnlyList<EventRecord>> ReadAllFromPrimaryAsync(Direction direction, long from, long head, int count, CancellationToken cancellationToken)
    {
        if (tenantId is null)
        {
            // Every tenant: the store's sessions are each one tenant's, so the page is read
            // through the mirror of the events table, which the wildcard reader leaves unfiltered.
            return await _virtual.ReadAllAsync(direction, from, head, count, cancellationToken).ConfigureAwait(false);
        }

        // The store's query hides archived events and scopes to the session's tenant on its own.
        await using var session = store.QuerySession(ToSessionOptions(tenantId));
        var query = session.Events.QueryAllRawEvents();
        var stored = direction == Direction.Forwards
            ? await PolecatQueryableExtensions.ToListAsync(query.Where(stored => stored.Sequence >= from && stored.Sequence <= head).OrderBy(stored => stored.Sequence).Take(count), cancellationToken).ConfigureAwait(false)
            : await PolecatQueryableExtensions.ToListAsync(query.Where(stored => stored.Sequence <= from).OrderByDescending(stored => stored.Sequence).Take(count), cancellationToken).ConfigureAwait(false);
        var records = new EventRecord[stored.Count];
        for (var i = 0; i < stored.Count; i++)
        {
            records[i] = ToRecord(stored[i]);
        }

        return records;
    }

    /// <inheritdoc/>
    public Task<StreamHead?> VirtualHeadAsync(VirtualStreamName stream, long head, CancellationToken cancellationToken) =>
        _virtual.HeadAsync(stream, head, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<EventRecord>> ReadVirtualAsync(VirtualStreamName stream, Direction direction, long from, long head, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return _replica is null
            ? _virtual.ReadAsync(stream, direction, from, head, count, cancellationToken)
            : _router.ReadAsync(
                direction,
                from,
                head,
                count,
                token => _replica.ReadBelowMarkAsync(stream, direction, from, head, count, token),
                (start, most, token) => _virtual.ReadAsync(stream, direction, start, head, most, token),
                cancellationToken);
    }

    /// <inheritdoc/>
    public Task<long> CountVirtualAsync(VirtualStreamName stream, long after, long head, CancellationToken cancellationToken) =>
        _virtual.CountAsync(stream, after, head, cancellationToken);

    /// <inheritdoc/>
    public Task<StreamHead?> OrdinalHeadAsync(VirtualStreamName stream, CancellationToken cancellationToken)
    {
        RequireOrdinals(stream.Name);
        RequireTenant(stream.Name);
        return _virtual.OrdinalHeadAsync(stream, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EventRecord>> ReadByOrdinalAsync(VirtualStreamName stream, Direction direction, long from, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        RequireOrdinals(stream.Name);
        RequireTenant(stream.Name);
        return _virtual.ReadByOrdinalAsync(stream, direction, from, count, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<long> NumberedThroughAsync(CancellationToken cancellationToken)
    {
        RequireOrdinals(StreamNames.All);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Named in full: the store's own LINQ has a method of the same name.
        var progress = context.SequencerProgress.AsNoTracking()
            .Where(row => row.Name == SequencerProgress.Ordinals)
            .Select(row => (long?)row.Position);
        return await EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(progress, cancellationToken).ConfigureAwait(false) ?? 0;
    }

    /// <summary>What the column the database named holds, as a caller would call it.</summary>
    private static string DescribeColumn(SqlException exception)
    {
        var column = ColumnRegex().Match(exception.Message) is { Success: true } match ? match.Groups[1].Value : string.Empty;
        return column switch
        {
            "id" or "stream_id" => ValueTooLongException.StreamName,
            "type" => "event type",
            "correlation_id" => "correlation id",
            "causation_id" => "causation id",
            _ => "value",
        };
    }

    [GeneratedRegex("column '([^']+)'")]
    private static partial Regex ColumnRegex();

    private void RequireOrdinals(string stream)
    {
        if (!ordinals)
        {
            throw new OrdinalsNotEnabledException(stream);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string stream, StreamState expected, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);

        // The check and the archive are two statements in one session; an append that lands between
        // them is archived with the rest, so the outcome is a deleted stream either way.
        await using var session = store.LightweightSession(ToSessionOptions(RequireTenant(stream)));
        var state = await session.Events.FetchStreamStateAsync(stream, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Version == 0)
        {
            throw new StreamNotFoundException(stream);
        }

        if (state.IsArchived)
        {
            throw new StreamDeletedException(stream);
        }

        CheckExpected(stream, expected, state.Version - 1);
        session.Events.ArchiveStream(stream);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task TombstoneAsync(string stream, StreamState expected, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);

        await using var session = store.LightweightSession(ToSessionOptions(RequireTenant(stream)));
        var state = await session.Events.FetchStreamStateAsync(stream, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Version == 0)
        {
            throw new StreamNotFoundException(stream);
        }

        CheckExpected(stream, expected, state.Version - 1);
        session.Events.TombstoneStream(stream);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A session's options for one tenant: the store scopes every read and write in the session to it.</summary>
    private static global::Polecat.SessionOptions ToSessionOptions(string tenant) => new() { TenantId = tenant };

    /// <summary>The tenant a stream name is read or written in: a name is per tenant, so under the wildcard there is none to use.</summary>
    private string RequireTenant(string stream) =>
        tenantId ?? throw new ArgumentException($"'{stream}' is a name within one tenant; the wildcard tenant reads $all and the virtual streams by position only.", nameof(stream));

    /// <summary>The expected state against the stream's current revision; any and stream-exists always pass here, because the stream exists.</summary>
    private static void CheckExpected(string stream, StreamState expected, long actual)
    {
        if (expected == StreamState.NoStream || (expected.HasRevision && expected.ToInt64() != actual))
        {
            throw new RevisionConflictException(stream, expected, actual);
        }
    }

    private static EventRecord ToRecord(IEvent stored) =>
        new(
            stored.Id,
            stored.StreamKey ?? string.Empty,
            stored.Version - 1,
            stored.Sequence,
            stored.EventTypeName,
            stored.Timestamp,
            stored.Data is JsonElement element ? JsonSerializer.SerializeToUtf8Bytes(element, NightingaleJson.Default) : JsonSerializer.SerializeToUtf8Bytes(stored.Data, NightingaleJson.Default),
            JsonEvent.GetMetadata(stored));

    /// <summary>
    /// After the store refused the append: either the same events are already there, from the
    /// expected revision on, and the retry succeeds with what the first attempt wrote, or the caller
    /// really is behind and learns the stream's actual revision.
    /// </summary>
    private async Task<AppendResult> ResolveConflict(string stream, StreamState expected, IReadOnlyList<EventData> events, CancellationToken cancellationToken)
    {
        await using var session = store.QuerySession(ToSessionOptions(RequireTenant(stream)));
        var state = await session.Events.FetchStreamStateAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = state is null ? -1 : state.Version - 1;
        if (state is null || (!expected.HasRevision && expected != StreamState.NoStream))
        {
            throw new RevisionConflictException(stream, expected, actual);
        }

        // The batch would have started at the revision after the expectation: revision 0 for a
        // stream expected not to exist, expected + 1 otherwise. One-based for the store.
        var firstVersion = expected == StreamState.NoStream ? 1 : expected.ToInt64() + 2;
        var stored = await session.Events.FetchStreamAsync(
            stream,
            version: firstVersion + events.Count - 1,
            fromVersion: firstVersion,
            token: cancellationToken).ConfigureAwait(false);
        if (stored.Count != events.Count)
        {
            throw new RevisionConflictException(stream, expected, actual);
        }

        for (var i = 0; i < events.Count; i++)
        {
            if (stored[i].Id != events[i].Id)
            {
                throw new RevisionConflictException(stream, expected, actual);
            }
        }

        var last = stored[^1];
        return new AppendResult(last.Version - 1, last.Sequence);
    }
}
