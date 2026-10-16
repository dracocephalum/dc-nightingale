using JasperFx.Events;
using Microsoft.Data.SqlClient;
using Polecat.Exceptions;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The append to several streams. One session holds every stream's append, so the store writes
/// them in one transaction, each checked against its expected state as a single append is, and
/// positions them stream by stream in the order given. Checks on streams not written are the
/// <see cref="StreamStateChecker"/>'s, added only when there are some. A metadata set is decided
/// as <see cref="SetStreamMetadataAsync"/> decides it and written as its events. When the store
/// refuses, every stream named is read once more, in one statement, and every condition that does
/// not hold is reported; what a single append would retry, a metadata set expecting any revision,
/// an append expecting no stream to a stream that is reset, is decided again and the whole append
/// written again.
/// </summary>
internal sealed partial class PolecatStreamStore
{
    /// <summary>SQL Server's error for a transaction chosen as a deadlock's victim: two appends that each check what the other writes.</summary>
    private const int Deadlock = 1205;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StreamAppendResult>> AppendToStreamsAsync(IReadOnlyList<StreamAppend> appends, IReadOnlyList<StreamCheck> checks, CancellationToken cancellationToken)
    {
        var problems = StreamAppends.FindProblems(appends, checks);
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(appends));
        }

        var streams = RequireStreams(appends[0].Stream);
        var tenant = RequireTenant(appends[0].Stream);

        // The appends expecting no stream that are written to a reset stream as its continuation,
        // by index, once the stream is found reset; as the single append's recreation does.
        var continued = new Dictionary<int, StreamState>();
        for (var attempt = 1; ; attempt++)
        {
            var writes = await PrepareWritesAsync(streams, appends, continued, cancellationToken).ConfigureAwait(false);
            try
            {
                if (await WriteOnceAsync(streams, tenant, writes, checks, cancellationToken).ConfigureAwait(false) is { } results)
                {
                    return results;
                }

                // Refused with no condition broken by the time it was read again, or a deadlock's
                // victim: written again, as a caller would.
                if (attempt == MetadataWriteAttempts)
                {
                    throw new InvalidOperationException("The store refused an append to several streams, and when every stream was read again each was in its expected state; the append was tried as many times as a metadata write is.");
                }
            }
            catch (StreamConflictsException conflicts) when (attempt < MetadataWriteAttempts)
            {
                // Decided again on what landed, and written again; anything else is the answer.
                if (!await TryDecideAgainAsync(streams, appends, conflicts, continued, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Whether every conflict is one a single append would decide again: a metadata set expecting
    /// any revision, decided again on what landed, or an append expecting no stream to a stream
    /// that is reset and has had nothing appended since, written as its continuation.
    /// </summary>
    private static async Task<bool> TryDecideAgainAsync(PlainStreamReader streams, IReadOnlyList<StreamAppend> appends, StreamConflictsException conflicts, Dictionary<int, StreamState> continued, CancellationToken cancellationToken)
    {
        var continuing = new Dictionary<int, StreamState>();
        foreach (var conflict in conflicts.Conflicts)
        {
            if (conflict.Index >= appends.Count)
            {
                return false;
            }

            switch (appends[conflict.Index])
            {
                case StreamAppend.OfMetadata { Expected: var expected } when expected == StreamState.Any:
                    continue;
                case StreamAppend.OfEvents { Expected: var expected } append when expected == StreamState.NoStream && conflict.ActualRevision >= 0
                    && !continued.ContainsKey(conflict.Index) && !StreamNames.TryParseMetadataStream(append.Stream, out _):
                    var state = await streams.ReadMetadataStateAsync(append.Stream, cancellationToken).ConfigureAwait(false);
                    if (state.ContinuesFrom != conflict.ActualRevision + 1)
                    {
                        return false;
                    }

                    continuing[conflict.Index] = StreamState.StreamRevision(conflict.ActualRevision);
                    continue;
                default:
                    return false;
            }
        }

        foreach (var (index, expected) in continuing)
        {
            continued[index] = expected;
        }

        return true;
    }

    /// <summary>Each append as the store writes it: the stream written, the expectation sent to the store, and the events, a metadata set's decided on its stream as read now.</summary>
    private static async Task<IReadOnlyList<Write>> PrepareWritesAsync(PlainStreamReader streams, IReadOnlyList<StreamAppend> appends, Dictionary<int, StreamState> continued, CancellationToken cancellationToken)
    {
        var writes = new List<Write>(appends.Count);
        for (var i = 0; i < appends.Count; i++)
        {
            switch (appends[i])
            {
                case StreamAppend.OfEvents events:
                    writes.Add(new Write(i, events, events.Stream, continued.TryGetValue(i, out var continues) ? continues : events.Expected, events.Events));
                    break;
                case StreamAppend.OfMetadata metadata:
                    var state = await streams.ReadMetadataStateAsync(metadata.Stream, cancellationToken).ConfigureAwait(false);
                    var expected = metadata.Expected == StreamState.Any ? ToExpected(state.MetadataRevision) : metadata.Expected;
                    writes.Add(new Write(i, metadata, metadata.Written, expected, ToMetadataEvents(ToMetadataBodies(state, metadata.Metadata))));
                    break;
            }
        }

        return writes;
    }

    /// <summary>
    /// Writes every append in one session, and the checks beside them. The results, in order; or,
    /// when the store refused, what reading every stream again says: the events already there, a
    /// conflict, or <see langword="null"/> to write it all again.
    /// </summary>
    private async Task<IReadOnlyList<StreamAppendResult>?> WriteOnceAsync(PlainStreamReader streams, string tenant, IReadOnlyList<Write> writes, IReadOnlyList<StreamCheck> checks, CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession(ToSessionOptions(tenant));
        var wrapped = new object[writes.Count][];
        var unmet = false;
        foreach (var write in writes)
        {
            var events = new object[write.Events.Count];
            List<(IEvent Event, ReadOnlyMemory<byte> BinaryData)>? binary = null;
            for (var i = 0; i < events.Length; i++)
            {
                var appended = JsonEvent.From(write.Events[i]);
                events[i] = appended;
                if (!write.Events[i].BinaryData.IsEmpty)
                {
                    (binary ??= []).Add((appended, write.Events[i].BinaryData));
                }
            }

            wrapped[write.Index] = events;
            if (binary is not null)
            {
                session.AddTransactionParticipant(new BinaryDataWriter(store.Options.DatabaseSchemaName, tenant, write.Stream, partitionedByTenant, binary));
            }

            if (write.Expected == StreamState.NoStream)
            {
                session.Events.StartStream(write.Stream, events);
            }
            else if (write.Expected == StreamState.StreamExists)
            {
                // As the single append checks it: before the write, without a lock. A stream that
                // is not there is a conflict, reported with the others when every stream is read.
                var state = await session.Events.FetchStreamStateAsync(write.Stream, cancellationToken).ConfigureAwait(false);
                unmet |= state is null || state.Version == 0;
                session.Events.Append(write.Stream, events);
            }
            else if (write.Expected == StreamState.Any)
            {
                session.Events.Append(write.Stream, events);
            }
            else
            {
                // The store's expected version is the version after the append, one-based.
                session.Events.Append(write.Stream, write.Expected.ToInt64() + 1 + events.Length, events);
            }
        }

        if (unmet)
        {
            return await ResolveConflictsAsync(streams, writes, checks, cancellationToken).ConfigureAwait(false);
        }

        if (checks.Count > 0)
        {
            session.AddTransactionParticipant(new StreamStateChecker(store.Options.DatabaseSchemaName, tenant, partitionedByTenant, checks, writes.Count));
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is EventStreamUnexpectedMaxEventIdException or JasperFx.Events.ExistingStreamIdCollisionException or JasperFx.Events.ArchivedStreamException)
        {
            return await ResolveConflictsAsync(streams, writes, checks, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number == Deadlock)
        {
            return null;
        }
        catch (SqlException exception) when (exception.Number == TooManyParameters)
        {
            throw new AppendSizeExceededException(writes[0].Stream, 0, exception);
        }
        catch (SqlException exception) when (exception.Number is TruncationWithColumn or Truncation)
        {
            throw new ValueTooLongException(DescribeColumn(exception), exception);
        }

        appended.Pulse();
        var results = new StreamAppendResult[writes.Count];
        foreach (var write in writes)
        {
            var last = (IEvent)wrapped[write.Index][^1];
            results[write.Index] = new StreamAppendResult(write.Stream, last.Version - 1, last.Sequence);
        }

        return results;
    }

    /// <summary>
    /// After the store refused: every stream named, read again in one statement. A stream written
    /// that is archived is the answer. Otherwise every condition that does not hold, against what
    /// the caller asked for, is a conflict, unless every append is already there with these very
    /// ids, from an attempt whose answer was lost, which only an append under an explicit revision
    /// or no stream can tell; then its results are the answer. Nothing broken, the append is
    /// written again.
    /// </summary>
    private static async Task<IReadOnlyList<StreamAppendResult>?> ResolveConflictsAsync(PlainStreamReader streams, IReadOnlyList<Write> writes, IReadOnlyList<StreamCheck> checks, CancellationToken cancellationToken)
    {
        var names = writes.Select(write => write.Stream).Concat(checks.Select(check => check.Stream)).ToList();
        var found = await streams.ReadRevisionsAsync(names, cancellationToken).ConfigureAwait(false);
        long ActualOf(string stream) => found.TryGetValue(stream, out var row) ? row.Last : -1;

        foreach (var write in writes)
        {
            if (found.TryGetValue(write.Stream, out var row) && row.IsArchived)
            {
                throw new StreamDeletedException(write.Stream);
            }
        }

        if (await TryFindWrittenAsync(streams, writes, ActualOf, cancellationToken).ConfigureAwait(false) is { } written)
        {
            return written;
        }

        var conflicts = new List<StreamConflict>();
        foreach (var write in writes)
        {
            var actual = ActualOf(write.Stream);
            if (!StreamAppends.Holds(write.Expected, actual))
            {
                conflicts.Add(new StreamConflict(write.Index, write.Stream, write.Append.Expected, actual));
            }
        }

        for (var i = 0; i < checks.Count; i++)
        {
            var actual = ActualOf(checks[i].Stream);
            if (!StreamAppends.Holds(checks[i].Expected, actual))
            {
                conflicts.Add(new StreamConflict(writes.Count + i, checks[i].Stream, checks[i].Expected, actual));
            }
        }

        return conflicts.Count > 0 ? throw new StreamConflictsException(conflicts) : null;
    }

    /// <summary>
    /// The results of an append already written, when every append is under an explicit revision
    /// or no stream and its stream holds these very ids from the revision it would have started
    /// at: the next after the revision expected, 0 for no stream, or, for a stream that is reset,
    /// the revision it continues from. <see langword="null"/> otherwise.
    /// </summary>
    private static async Task<IReadOnlyList<StreamAppendResult>?> TryFindWrittenAsync(PlainStreamReader streams, IReadOnlyList<Write> writes, Func<string, long> actualOf, CancellationToken cancellationToken)
    {
        if (writes.Any(write => write.Append is not StreamAppend.OfEvents || !(write.Expected.HasRevision || write.Expected == StreamState.NoStream)))
        {
            return null;
        }

        var results = new StreamAppendResult[writes.Count];
        foreach (var write in writes)
        {
            var first = write.Expected.HasRevision ? write.Expected.ToInt64() + 1 : 0;
            if (write.Expected == StreamState.NoStream && actualOf(write.Stream) >= 0
                && (await streams.ReadMetadataStateAsync(write.Stream, cancellationToken).ConfigureAwait(false)).ContinuesFrom is { } continuesFrom)
            {
                first = continuesFrom;
            }

            var stored = await streams.ReadIdsAsync(write.Stream, first, write.Events.Count, cancellationToken).ConfigureAwait(false);
            if (stored.Count != write.Events.Count || stored.Where((id, i) => id.Id != write.Events[i].Id).Any())
            {
                return null;
            }

            results[write.Index] = new StreamAppendResult(write.Stream, stored[^1].Revision, stored[^1].Position);
        }

        return results;
    }

    /// <summary>One append as the store writes it.</summary>
    /// <param name="Index">Its place among the appends.</param>
    /// <param name="Append">The append as the caller gave it, whose expectation a conflict reports.</param>
    /// <param name="Stream">The stream written.</param>
    /// <param name="Expected">The expectation sent to the store: the caller's, a metadata set's decided revision, or a reset stream's continuation.</param>
    /// <param name="Events">The events written.</param>
    private sealed record Write(int Index, StreamAppend Append, string Stream, StreamState Expected, IReadOnlyList<EventData> Events);
}
