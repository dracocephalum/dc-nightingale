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
/// Every read goes through the gateway's mirror of the store's tables: a plain stream's with its
/// metadata applied (<see cref="PlainStreamReader"/>), <c>$all</c> and the virtual streams by
/// position or ordinal (<see cref="VirtualStreamReader"/>). With a read-only connection, a page of
/// <c>$all</c> or of a virtual stream by position is taken from it as far as it reaches and from
/// the main connection beyond, so the answer is the one the main connection alone would give; a
/// bounded read of a plain stream is taken from it only when the host asks, and may then lag.
/// Everything else uses the main connection.
/// </para>
/// </summary>
/// <param name="store">The store.</param>
/// <param name="tenantId">The tenant every session is opened for, or <see langword="null"/> for all tenants, under the wildcard: then <c>$all</c> and the virtual streams read across tenants and a stream name, which is per tenant, is refused.</param>
/// <param name="events">Makes the mirror of the store's tables over the main connection.</param>
/// <param name="ordinals">Whether the store was initialized with ordinals.</param>
/// <param name="readOnlyEvents">Makes the same mirror over the read-only connection, or <see langword="null"/> when the host does not read through it.</param>
/// <param name="readOnlyStreams">Makes the same mirror over the read-only connection for bounded reads of plain streams, or <see langword="null"/> when they stay on the main connection.</param>
/// <param name="appended">Pulsed after each append commits, so the tail looks at once instead of at its next poll.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class PolecatStreamStore(IDocumentStore store, string? tenantId, IDbContextFactory<EventsDbContext> events, bool ordinals, IDbContextFactory<ReadOnlyEventsDbContext>? readOnlyEvents, IDbContextFactory<ReadOnlyEventsDbContext>? readOnlyStreams, AppendSignal appended, TimeProvider timeProvider, ILogger<PolecatStreamStore> logger) : IStreamStore
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
    private readonly PlainStreamReader? _streams = tenantId is null ? null : new(events.CreateDbContext, tenantId, logger);
    private readonly PlainStreamReader? _replicaStreams = tenantId is null || readOnlyStreams is null ? null : new(readOnlyStreams.CreateDbContext, tenantId, logger);

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

        // Committed, so the tail finds the rows the moment it looks; an append resolved as a
        // retry of one that already happened pulsed when it happened. Deletes and tombstones
        // move no head and do not pulse.
        appended.Pulse();
        var last = (IEvent)wrapped[^1];
        return new AppendResult(last.Version - 1, last.Sequence);
    }

    /// <inheritdoc/>
    public Task<StreamSlice?> ReadAsync(string stream, Direction direction, long? from, int count, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return RequireStreams(stream).ReadAsync(stream, direction, from, count, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<StreamSlice?> ReadEventualAsync(string stream, Direction direction, long? from, int count, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var main = RequireStreams(stream);
        return _replicaStreams is null
            ? main.ReadAsync(stream, direction, from, count, cancellationToken)
            : _router.PreferReplicaAsync(
                token => _replicaStreams.ReadAsync(stream, direction, from, count, token),
                token => main.ReadAsync(stream, direction, from, count, token),
                cancellationToken);
    }

    /// <inheritdoc/>
    public Task<StreamSlice?> ReadAsStoredAsync(string stream, long from, int count, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return RequireStreams(stream).ReadAsStoredAsync(stream, from, count, cancellationToken);
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

    /// <summary>
    /// A page of <c>$all</c> from the main connection, through the mirror of the events table for
    /// every tenant alike: the mirror scopes to the tenant, or to all of them under the wildcard,
    /// hides archived events, and hydrates a row with its headers. The store's own raw-events query
    /// does not read the headers column (<c>UPSTREAM.md</c>), so a page read through it came back
    /// without the metadata the events carry, which the read-only connection, serving most pages,
    /// kept from view until a page fell to the main connection.
    /// </summary>
    private Task<IReadOnlyList<EventRecord>> ReadAllFromPrimaryAsync(Direction direction, long from, long head, int count, CancellationToken cancellationToken) =>
        _virtual.ReadAllAsync(direction, from, head, count, cancellationToken);

    /// <inheritdoc/>
    public Task<ScannedSlice> ScanAllAsync(EventFilter filter, Direction direction, long from, long head, int window, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(window);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // The main connection, whichever tenant: the mirror scopes the stretch to the tenant, or
        // to all of them under the wildcard, as it does for every read of $all by position.
        return _virtual.ScanAllAsync(filter, direction, from, head, window, count, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EventPointer>> ReadPointersAsync(long from, long head, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // Always the main connection: the router routes what the tail says is committed, and
        // a replica may not hold it yet.
        if (tenantId is null)
        {
            throw new InvalidOperationException("A stream name is per tenant, so the wildcard tenant has no streams to route.");
        }

        return _virtual.ReadPointersAsync(from, head, count, cancellationToken);
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
    public async Task ArchiveAsync(string stream, StreamState expected, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);

        // The check and the archive are two statements in one session; an append that lands between
        // them is archived with the rest, so the outcome is an archived stream either way.
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
    public async Task DeleteAsync(string stream, StreamState expected, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);

        await using var session = store.LightweightSession(ToSessionOptions(RequireTenant(stream)));
        var state = await session.Events.FetchStreamStateAsync(stream, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Version == 0)
        {
            throw new StreamNotFoundException(stream);
        }

        CheckExpected(stream, expected, state.Version - 1);

        // Its metadata goes with it, in the same transaction: the name is free again afterwards,
        // and metadata left behind would be in force on whatever stream takes it next. The
        // store's tombstone deletes by name, so a stream whose metadata was never set loses
        // nothing more.
        session.Events.TombstoneStream(stream);
        session.Events.TombstoneStream(StreamNames.ToMetadataStream(stream));
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<StreamMetadataResult> GetStreamMetadataAsync(string stream, CancellationToken cancellationToken)
    {
        var metastream = StreamNames.ToMetadataStream(stream);
        var page = await ReadAsync(metastream, Direction.Backwards, null, 1, cancellationToken).ConfigureAwait(false);
        if (page is null || page.Events.Count == 0)
        {
            return new StreamMetadataResult(stream, -1, StreamMetadata.Empty);
        }

        var last = page.Events[0];
        return new StreamMetadataResult(stream, last.Revision, StreamMetadata.FromJson(last.Data.Span));
    }

    /// <inheritdoc/>
    public Task<AppendResult> SetStreamMetadataAsync(string stream, StreamState expected, StreamMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var metastream = StreamNames.ToMetadataStream(stream);

        // A new id every time, as the reference's client gives each set: two sets of the same
        // metadata are two changes, and a retry after a lost answer reads the metadata back.
        var change = new EventData(Guid.NewGuid(), StreamNames.MetadataEventType, metadata.ToJsonBytes());
        return AppendAsync(metastream, expected, [change], cancellationToken);
    }

    /// <summary>A session's options for one tenant: the store scopes every read and write in the session to it.</summary>
    private static global::Polecat.SessionOptions ToSessionOptions(string tenant) => new() { TenantId = tenant };

    /// <summary>The tenant a stream name is read or written in: a name is per tenant, so under the wildcard there is none to use.</summary>
    private string RequireTenant(string stream) =>
        tenantId ?? throw CreateWildcardException(stream);

    /// <summary>The reader of the tenant's plain streams; under the wildcard there is none.</summary>
    private PlainStreamReader RequireStreams(string stream) =>
        _streams ?? throw CreateWildcardException(stream);

    private static ArgumentException CreateWildcardException(string stream) =>
        new($"'{stream}' is a name within one tenant; the wildcard tenant reads $all and the virtual streams by position only.", nameof(stream));

    /// <summary>The expected state against the stream's current revision; any and stream-exists always pass here, because the stream exists.</summary>
    private static void CheckExpected(string stream, StreamState expected, long actual)
    {
        if (expected == StreamState.NoStream || (expected.HasRevision && expected.ToInt64() != actual))
        {
            throw new RevisionConflictException(stream, expected, actual);
        }
    }

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
