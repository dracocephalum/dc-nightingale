using System.Text.Json;

using JasperFx.Events;
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
/// </summary>
/// <param name="store">The store.</param>
/// <param name="connectionString">The connection string the store uses, for the virtual streams read straight from the table.</param>
internal sealed class PolecatStreamStore(IDocumentStore store, string connectionString) : IStreamStore
{
    private readonly VirtualStreamReader _virtual = new(connectionString, store.Options.DatabaseSchemaName, JasperFx.StorageConstants.DefaultTenantId);

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

        await using var session = store.LightweightSession();
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

        var last = (IEvent)wrapped[^1];
        return new AppendResult(last.Version - 1, last.Sequence);
    }

    /// <inheritdoc/>
    public async Task<StreamSlice?> ReadAsync(string stream, Direction direction, long? from, int count, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        await using var session = store.QuerySession();
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
    public async Task<IReadOnlyList<EventRecord>> ReadAllAsync(Direction direction, long from, long head, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // The store's query hides archived events and scopes to the session's tenant on its own.
        await using var session = store.QuerySession();
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
        return _virtual.ReadAsync(stream, direction, from, head, count, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<long> CountVirtualAsync(VirtualStreamName stream, long after, long head, CancellationToken cancellationToken) =>
        _virtual.CountAsync(stream, after, head, cancellationToken);

    private static EventRecord ToRecord(IEvent stored) =>
        new(
            stored.Id,
            stored.StreamKey ?? string.Empty,
            stored.Version - 1,
            stored.Sequence,
            stored.EventTypeName,
            stored.Timestamp,
            stored.Data is JsonElement element ? JsonSerializer.SerializeToUtf8Bytes(element, NightingaleJson.Options) : JsonSerializer.SerializeToUtf8Bytes(stored.Data, NightingaleJson.Options),
            JsonEvent.MetadataOf(stored));

    /// <summary>
    /// After the store refused the append: either the same events are already there, from the
    /// expected revision on, and the retry succeeds with what the first attempt wrote, or the caller
    /// really is behind and learns the stream's actual revision.
    /// </summary>
    private async Task<AppendResult> ResolveConflict(string stream, StreamState expected, IReadOnlyList<EventData> events, CancellationToken cancellationToken)
    {
        await using var session = store.QuerySession();
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
