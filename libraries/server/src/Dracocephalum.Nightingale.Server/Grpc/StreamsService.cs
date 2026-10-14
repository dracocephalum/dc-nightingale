using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Dracocephalum.Nightingale.Server.Auth;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server.Grpc;

/// <summary>
/// The <c>Streams</c> service over the <see cref="IStreamStore"/> and the <see cref="IStoreTail"/>
/// of the call's tenant. It validates requests, keeps the contract's message order on
/// a read, and translates the store's domain exceptions into the contract's errors. It knows
/// nothing about the backend: revisions and positions arrive from the store already in the
/// contract's numbering. It serves plain streams, their metadata streams, <c>$all</c> and the
/// virtual streams, bounded and as subscriptions, the virtual streams under either numbering but
/// the data events by position only, and <c>$all</c> under a filter; and it sets and reads a
/// stream's metadata.
/// </summary>
/// <param name="stores">The backend's stores and tails, one per tenant; a call resolves those of the tenant it was authorized for.</param>
/// <param name="routers">The routers a subscription to a plain stream takes its live events through, one per tenant.</param>
/// <param name="timeProvider">The clock the progress notes are stamped with.</param>
/// <param name="options">The common settings: which deletions the host allows.</param>
public sealed class StreamsService(ITenantStores stores, StreamRouters routers, TimeProvider timeProvider, NightingaleOptionsBase options) : Streams.StreamsBase
{
    /// <summary>The longest stream name the store column holds.</summary>
    public const int MaxStreamNameLength = 250;

    /// <summary>
    /// How many events one store call asks for. Small enough that a slow consumer never makes the
    /// server hold a large page in memory, large enough that a full read is not one call per event.
    /// </summary>
    public const int PageSize = 512;

    /// <summary>
    /// The most positions one scan of <c>$all</c> under a filter examines: what bounds a call
    /// over a stretch the filter keeps nothing of, and what a subscription's checkpoint advances
    /// by at most at a time.
    /// </summary>
    public const int ScanWindow = EventSource.ScanWindow;

    /// <summary>
    /// After how many positions examined without an event to deliver a filtered subscription is
    /// sent a checkpoint, unless the filter says otherwise: the reference's window times its
    /// multiplier, sized for a scan window rather than for its batch of thirty-two.
    /// </summary>
    public const int DefaultCheckpointInterval = 1024;

    /// <inheritdoc/>
    public override async Task Read(ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        var scope = await context.AuthorizeAsync(CredentialRole.User, TenantAccess.Read).ConfigureAwait(false);
        var store = stores.GetStreams(scope);
        var tail = stores.GetTail(scope);

        var stream = RequireValidStreamName(request.Stream);
        var all = stream == StreamNames.All;
        var isVirtual = StreamNames.TryParseVirtual(stream, out var virtualStream);
        EventFilter? filter = null;
        var checkpointInterval = DefaultCheckpointInterval;
        if (request.Filter is not null)
        {
            if (!all)
            {
                throw NightingaleErrors.FilterNotAllowed(stream);
            }

            try
            {
                filter = request.Filter.ToEventFilter();
            }
            catch (ArgumentException exception)
            {
                throw NightingaleErrors.InvalidArgument(exception.Message);
            }

            if (request.Filter.CheckpointInterval > 0)
            {
                checkpointInterval = (int)Math.Min(request.Filter.CheckpointInterval, int.MaxValue);
            }
        }

        var ordinal = request.Numbering.ToNumbering() == Numbering.Ordinal;
        if (ordinal && !(isVirtual && virtualStream.HasOrdinals))
        {
            throw NightingaleErrors.InvalidArgument("Ordinal numbering applies to $ce-, $et- and $mde streams only.");
        }

        if (ordinal && !store.OrdinalsEnabled)
        {
            throw NightingaleErrors.OrdinalsNotEnabled(stream);
        }

        var direction = request.Direction == ReadDirection.Backwards ? Direction.Backwards : Direction.Forwards;
        if (request.FromCase == ReadRequest.FromOneofCase.Position && request.Position < 0)
        {
            throw NightingaleErrors.InvalidArgument("A position is never negative.");
        }

        var cancellationToken = context.CancellationToken;
        switch (request.ModeCase)
        {
            case ReadRequest.ModeOneofCase.Count when request.Count > 0:
                if (all)
                {
                    await ReadAllBounded(store, tail, request, direction, filter, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else if (ordinal)
                {
                    await ReadOrdinalBounded(store, virtualStream, request, direction, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else if (isVirtual)
                {
                    await ReadVirtualBounded(store, tail, virtualStream, request, direction, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReadStreamBounded(store, stream, request, direction, responseStream, cancellationToken).ConfigureAwait(false);
                }

                return;
            case ReadRequest.ModeOneofCase.Subscription:
                if (direction == Direction.Backwards)
                {
                    throw NightingaleErrors.InvalidArgument("A subscription reads forwards.");
                }

                if (all && filter is not null)
                {
                    await SubscribeAllFiltered(store, tail, routers.For(scope), request, filter, checkpointInterval, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else if (all)
                {
                    await SubscribeAll(store, tail, routers.For(scope), request, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else if (ordinal)
                {
                    await SubscribeOrdinal(store, tail, virtualStream, request, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else if (isVirtual)
                {
                    await SubscribeVirtual(store, tail, routers.For(scope), virtualStream, request, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SubscribeStream(store, routers.For(scope), stream, request, responseStream, cancellationToken).ConfigureAwait(false);
                }

                return;
            default:
                throw NightingaleErrors.InvalidArgument("A read needs a positive count.");
        }
    }

    /// <inheritdoc/>
    public override async Task<AppendResponse> Append(IAsyncStreamReader<AppendRequest> requestStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(context);
        var store = stores.GetStreams(await context.AuthorizeAsync(CredentialRole.User, TenantAccess.Write).ConfigureAwait(false));

        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false)
            || requestStream.Current.ContentCase != AppendRequest.ContentOneofCase.Options)
        {
            throw NightingaleErrors.InvalidArgument("The first message of an append carries the options.");
        }

        var appendOptions = requestStream.Current.Options;
        var stream = RequirePlainStreamName(appendOptions.Stream);
        var expected = ToStreamState(appendOptions.ExpectedRevision);

        var events = new List<EventData>();
        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            if (requestStream.Current.ContentCase != AppendRequest.ContentOneofCase.Event)
            {
                throw NightingaleErrors.InvalidArgument("After the options, every message of an append carries one event.");
            }

            // Refused at the first event too many, before the rest is received or anything is
            // written: an append is held in full until it is stored.
            if (events.Count == options.MaxEventsPerAppend)
            {
                throw NightingaleErrors.AppendSizeExceeded(stream, options.MaxEventsPerAppend);
            }

            var proposed = requestStream.Current.Event;
            if (string.IsNullOrEmpty(proposed.EventType))
            {
                throw NightingaleErrors.InvalidArgument("Every event needs a type.");
            }

            try
            {
                events.Add(proposed.ToEventData());
            }
            catch (FormatException)
            {
                throw NightingaleErrors.InvalidArgument("An event id is a UUID in its canonical form.");
            }
            catch (System.Text.Json.JsonException)
            {
                throw NightingaleErrors.InvalidArgument("Event metadata is a JSON object.");
            }
        }

        if (events.Count == 0)
        {
            throw NightingaleErrors.InvalidArgument("An append carries at least one event.");
        }

        try
        {
            var result = await store.AppendAsync(stream, expected, events, context.CancellationToken).ConfigureAwait(false);
            return new AppendResponse { Revision = result.Revision, Position = result.Position };
        }
        catch (RevisionConflictException conflict)
        {
            throw NightingaleErrors.RevisionConflict(conflict);
        }
        catch (StreamDeletedException deleted)
        {
            throw NightingaleErrors.StreamDeleted(deleted.Stream);
        }
        catch (AppendSizeExceededException)
        {
            // The store refused what the limit let through: the limit is set above what this
            // version of the store takes. The caller is told the same thing either way.
            throw NightingaleErrors.AppendSizeExceeded(stream, options.MaxEventsPerAppend);
        }
        catch (ValueTooLongException tooLong) when (tooLong.What == ValueTooLongException.StreamName)
        {
            throw NightingaleErrors.InvalidStreamName(stream, "it is longer than the store holds");
        }
        catch (ValueTooLongException tooLong)
        {
            throw NightingaleErrors.InvalidArgument(tooLong.Message);
        }
    }

    /// <inheritdoc/>
    public override async Task<DeleteResponse> Delete(DeleteRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var store = stores.GetStreams(await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false));

        var stream = RequirePlainStreamName(request.Stream);
        if (!options.Deletion.AllowDelete)
        {
            throw NightingaleErrors.DeletionDisabled(stream, "Delete");
        }

        await RunDeletionAsync(stream, () => store.DeleteAsync(stream, ToStreamState(request.ExpectedRevision), context.CancellationToken)).ConfigureAwait(false);
        return new DeleteResponse();
    }

    /// <inheritdoc/>
    public override async Task<TombstoneResponse> Tombstone(TombstoneRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var store = stores.GetStreams(await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false));

        var stream = RequirePlainStreamName(request.Stream);
        if (!options.Deletion.AllowTombstone)
        {
            throw NightingaleErrors.DeletionDisabled(stream, "Tombstone");
        }

        await RunDeletionAsync(stream, () => store.TombstoneAsync(stream, ToStreamState(request.ExpectedRevision), context.CancellationToken)).ConfigureAwait(false);
        return new TombstoneResponse();
    }

    /// <inheritdoc/>
    public override async Task<GetStreamMetadataResponse> GetStreamMetadata(GetStreamMetadataRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var store = stores.GetStreams(await context.AuthorizeAsync(CredentialRole.User, TenantAccess.Read).ConfigureAwait(false));

        var stream = RequireMetadataStreamName(request.Stream);
        var result = await store.GetStreamMetadataAsync(stream, context.CancellationToken).ConfigureAwait(false);
        return new GetStreamMetadataResponse { Revision = result.Revision, Metadata = result.Metadata.ToWire() };
    }

    /// <inheritdoc/>
    public override async Task<SetStreamMetadataResponse> SetStreamMetadata(SetStreamMetadataRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var store = stores.GetStreams(await context.AuthorizeAsync(CredentialRole.User, TenantAccess.Write).ConfigureAwait(false));

        var stream = RequireMetadataStreamName(request.Stream);
        var expected = ToStreamState(request.ExpectedRevision);
        StreamMetadata metadata;
        try
        {
            metadata = request.Metadata.ToStreamMetadata();
            metadata.Validate();
        }
        catch (System.Text.Json.JsonException)
        {
            throw NightingaleErrors.InvalidArgument("Custom stream metadata is a JSON object.");
        }
        catch (ArgumentException invalid)
        {
            throw NightingaleErrors.InvalidArgument(invalid.Message);
        }

        try
        {
            var result = await store.SetStreamMetadataAsync(stream, expected, metadata, context.CancellationToken).ConfigureAwait(false);
            return new SetStreamMetadataResponse { Revision = result.Revision, Position = result.Position };
        }
        catch (RevisionConflictException conflict)
        {
            throw NightingaleErrors.RevisionConflict(conflict);
        }
    }

    private static StreamState ToStreamState(long expectedRevision)
    {
        try
        {
            return StreamState.FromInt64(expectedRevision);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw NightingaleErrors.InvalidArgument("The expected revision is neither a revision nor a named state.");
        }
    }

    private static async Task RunDeletionAsync(string stream, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (StreamNotFoundException)
        {
            throw NightingaleErrors.StreamNotFound(stream);
        }
        catch (StreamDeletedException)
        {
            throw NightingaleErrors.StreamDeleted(stream);
        }
        catch (RevisionConflictException conflict)
        {
            throw NightingaleErrors.RevisionConflict(conflict);
        }
    }

    /// <summary>
    /// Validates a name where a plain stream is required. Reserved names are valid in the contract but
    /// not appended to.
    /// </summary>
    private static string RequirePlainStreamName(string stream)
    {
        var name = RequireValidStreamName(stream);
        if (StreamNames.IsReserved(name))
        {
            throw NightingaleErrors.InvalidStreamName(name, "reserved; only plain streams are appended to");
        }

        return name;
    }

    /// <summary>
    /// Validates a name for a read: a plain stream, <c>$all</c>, a virtual stream, or a plain
    /// stream's metadata stream, which reads as the plain stream it is in the store. Any other
    /// reserved name is invalid.
    /// </summary>
    private static string RequireValidStreamName(string stream)
    {
        if (string.IsNullOrEmpty(stream))
        {
            throw NightingaleErrors.InvalidStreamName(stream, "empty");
        }

        if (stream.Length > MaxStreamNameLength)
        {
            throw NightingaleErrors.InvalidStreamName(stream, $"longer than {MaxStreamNameLength} characters");
        }

        if (StreamNames.IsReserved(stream) && stream != StreamNames.All && !StreamNames.TryParseVirtual(stream, out _) && !StreamNames.TryParseMetadataStream(stream, out _))
        {
            throw NightingaleErrors.InvalidStreamName(stream, "reserved and not $all, $ce-<category>, $et-<event type>, $de, $mde or $$<stream>");
        }

        return stream;
    }

    /// <summary>
    /// Validates the name of a stream whose metadata is read or set: a plain stream's, short enough
    /// that its metadata stream's name, two characters longer, still fits the store's column.
    /// </summary>
    private static string RequireMetadataStreamName(string stream)
    {
        var name = RequirePlainStreamName(stream);
        if (name.Length + StreamNames.MetadataPrefix.Length > MaxStreamNameLength)
        {
            throw NightingaleErrors.InvalidStreamName(name, $"longer than {MaxStreamNameLength - StreamNames.MetadataPrefix.Length} characters, so its metadata stream's name would not fit");
        }

        return name;
    }

    private static ReadResponse CaughtUpAt(long head, DateTimeOffset at) =>
        new() { CaughtUp = new CaughtUp { Head = head, At = Timestamp.FromDateTimeOffset(at) } };

    private static ReadResponse CheckpointAt(long position, DateTimeOffset at) =>
        new() { Checkpoint = new Checkpoint { Position = position, At = Timestamp.FromDateTimeOffset(at) } };

    private static ReadResponse FellBehindAt(long head, long behind, DateTimeOffset at) =>
        new() { FellBehind = new FellBehind { Head = head, EventsBehind = behind, At = Timestamp.FromDateTimeOffset(at) } };

    private static async Task ReadStreamBounded(IStreamStore store, string stream, ReadRequest request, Direction direction, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        long? from = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.Start => direction == Direction.Forwards ? null : 0,
            ReadRequest.FromOneofCase.End => direction == Direction.Backwards ? null : long.MaxValue,
            _ => null,
        };

        var remaining = request.Count;
        var first = true;
        while (remaining > 0)
        {
            var pageSize = (int)Math.Min(remaining, PageSize);
            var page = await ReadPage(store, stream, direction, from, pageSize, cancellationToken, eventual: true).ConfigureAwait(false);
            if (page is null)
            {
                await responseStream.WriteAsync(new ReadResponse { StreamNotFound = new StreamNotFound { Stream = stream } }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (first)
            {
                await responseStream.WriteAsync(new ReadResponse { Head = new StreamBounds { First = page.Head.First, Last = page.Head.Last } }, cancellationToken).ConfigureAwait(false);
                first = false;
            }

            foreach (var record in page.Events)
            {
                await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
            }

            remaining -= (ulong)page.Events.Count;
            if (page.Events.Count < pageSize)
            {
                return;
            }

            var last = page.Events[^1].Revision;
            if (direction == Direction.Backwards && last == 0)
            {
                return;
            }

            from = direction == Direction.Forwards ? last + 1 : last - 1;
        }
    }

    /// <summary>
    /// A bounded read of <c>$all</c> is a snapshot: the head is brought up to date once, before the
    /// first page, so an append that returned before the read is in it, and no page reads past it,
    /// so events committed during the read are not delivered.
    /// </summary>
    private static async Task ReadAllBounded(IStreamStore store, IStoreTail tail, ReadRequest request, Direction direction, EventFilter? filter, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var head = await tail.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var from = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => direction == Direction.Backwards ? head : head + 1,
            _ => direction == Direction.Forwards ? 0 : head,
        };

        await responseStream.WriteAsync(new ReadResponse { Head = new StreamBounds { First = 0, Last = head } }, cancellationToken).ConfigureAwait(false);
        var remaining = request.Count;
        if (filter is not null)
        {
            // Stretch by stretch to the end, whatever each keeps; the scan says how far it looked.
            while (remaining > 0 && head > 0 && (direction == Direction.Forwards ? from <= head : from >= 1))
            {
                var slice = await store.ScanAllAsync(filter, direction, from, head, ScanWindow, (int)Math.Min(remaining, PageSize), cancellationToken).ConfigureAwait(false);
                foreach (var record in slice.Events)
                {
                    await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                }

                remaining -= (ulong)slice.Events.Count;
                from = direction == Direction.Forwards ? slice.Through + 1 : slice.Through - 1;
            }

            return;
        }

        while (remaining > 0)
        {
            if (head == 0 || (direction == Direction.Forwards ? from > head : from <= 0))
            {
                return;
            }

            var pageSize = (int)Math.Min(remaining, PageSize);
            var page = await store.ReadAllAsync(direction, from, head, pageSize, cancellationToken).ConfigureAwait(false);
            foreach (var record in page)
            {
                await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
            }

            remaining -= (ulong)page.Count;
            if (page.Count < pageSize)
            {
                return;
            }

            from = direction == Direction.Forwards ? page[^1].Position + 1 : page[^1].Position - 1;
        }
    }

    /// <summary>
    /// A subscription to a plain stream: confirm with the head, deliver what is there, say so, then
    /// deliver what the router brings, in revision order, saying so after each batch. A stream that
    /// does not exist yet is confirmed with head -1 and waited for. The router is followed before
    /// the first read, so an event the read does not see yet is one the router brings; what the
    /// router brings ahead of the next revision, or a lapse for leaving too much unread, means the
    /// stream is read again from where the delivery stands, under a fresh following.
    /// </summary>
    private async Task SubscribeStream(IStreamStore store, StreamRouter router, string stream, ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var liveOnly = request.FromCase == ReadRequest.FromOneofCase.End;
        var next = request.FromCase == ReadRequest.FromOneofCase.Position ? request.Position : 0;

        var following = router.Follow(stream);
        try
        {
            var page = await ReadPage(store, stream, Direction.Forwards, liveOnly ? long.MaxValue : next, PageSize, cancellationToken).ConfigureAwait(false);
            var head = page?.Head.Last ?? -1;
            await responseStream.WriteAsync(new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = id, Head = head } }, cancellationToken).ConfigureAwait(false);
            if (liveOnly)
            {
                next = head + 1;
                page = null;
            }

            while (true)
            {
                var behind = false;
                while (page is not null && page.Events.Count > 0)
                {
                    if (page.Events.Count == PageSize && page.Head.Last > page.Events[^1].Revision && !behind)
                    {
                        behind = true;
                        await responseStream.WriteAsync(FellBehindAt(page.Head.Last, page.Head.Last - next + 1, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    }

                    foreach (var record in page.Events)
                    {
                        await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                    }

                    next = page.Events[^1].Revision + 1;
                    head = page.Head.Last;
                    page = page.Events.Count < PageSize ? null : await ReadPage(store, stream, Direction.Forwards, next, PageSize, cancellationToken).ConfigureAwait(false);
                }

                await responseStream.WriteAsync(CaughtUpAt(head, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);

                var ahead = false;
                while (!ahead && await WaitForLiveAsync(following, cancellationToken).ConfigureAwait(false))
                {
                    var delivered = false;
                    while (following.Events.TryRead(out var record))
                    {
                        if (record.Revision < next)
                        {
                            continue;
                        }

                        if (record.Revision > next)
                        {
                            ahead = true;
                            break;
                        }

                        await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                        next = record.Revision + 1;
                        head = record.Revision;
                        delivered = true;
                    }

                    if (delivered && !ahead)
                    {
                        await responseStream.WriteAsync(CaughtUpAt(head, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    }
                }

                following.Dispose();
                following = router.Follow(stream);
                page = await ReadPage(store, stream, Direction.Forwards, next, PageSize, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            following.Dispose();
        }
    }

    /// <summary>Waits for the router to bring something; a stream deleted meanwhile is the contract's error, as it is on a read.</summary>
    private static async Task<bool> WaitForLiveAsync(StreamFollowing following, CancellationToken cancellationToken)
    {
        try
        {
            return await following.Events.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (StreamDeletedException deleted)
        {
            throw NightingaleErrors.StreamDeleted(deleted.Stream);
        }
    }

    /// <summary>
    /// A subscription to <c>$all</c>: the same shape as a stream's, with the store's head as the
    /// bound of every catch-up page, then what the router brings, saying so after each batch with
    /// the last position delivered. Followed before the head is taken, so an event the catch-up
    /// does not see is one the router brings; what was delivered already is skipped. A lapse means
    /// catching up again from where the delivery stands, under a fresh following.
    /// </summary>
    private async Task SubscribeAll(IStreamStore store, IStoreTail tail, StreamRouter router, ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var following = router.Follow(StreamNames.All);
        try
        {
            // From the end means after now, so the head is brought up to date first; from anywhere
            // else the catch-up reads whatever the poller has seen and the router delivers the rest.
            var head = request.FromCase == ReadRequest.FromOneofCase.End ? await tail.RefreshAsync(cancellationToken).ConfigureAwait(false) : tail.Head;
            var next = request.FromCase switch
            {
                ReadRequest.FromOneofCase.Position => request.Position,
                ReadRequest.FromOneofCase.End => head + 1,
                _ => 0,
            };
            await responseStream.WriteAsync(new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = id, Head = head } }, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var behind = false;
                while (head > 0 && next <= head)
                {
                    var page = await store.ReadAllAsync(Direction.Forwards, next, head, PageSize, cancellationToken).ConfigureAwait(false);
                    if (page.Count == PageSize && page[^1].Position < head && !behind)
                    {
                        behind = true;
                        await responseStream.WriteAsync(FellBehindAt(head, head - page[^1].Position, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    }

                    foreach (var record in page)
                    {
                        await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                    }

                    next = page.Count < PageSize ? head + 1 : page[^1].Position + 1;
                }

                await responseStream.WriteAsync(CaughtUpAt(head, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);

                while (await following.Events.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var delivered = false;
                    while (following.Events.TryRead(out var record))
                    {
                        if (record.Position < next)
                        {
                            continue;
                        }

                        await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                        next = record.Position + 1;
                        head = record.Position;
                        delivered = true;
                    }

                    if (delivered)
                    {
                        await responseStream.WriteAsync(CaughtUpAt(head, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    }
                }

                following.Dispose();
                following = router.Follow(StreamNames.All);
                head = tail.Head;
            }
        }
        finally
        {
            following.Dispose();
        }
    }

    /// <summary>
    /// A subscription to <c>$all</c> under a filter: the shape of the unfiltered one, catching up
    /// stretch by stretch through the store's scan and then keeping, of what the router brings,
    /// what the filter keeps. Positions examined without an event to deliver are counted, and at
    /// the interval a checkpoint carries the last of them, so a consumer's position advances over
    /// a quiet stretch; an event delivered starts the count again. The head said with caught up
    /// is the last position examined. No fell-behind: how many events a stretch holds for the
    /// filter is not known until it is scanned.
    /// </summary>
    private async Task SubscribeAllFiltered(IStreamStore store, IStoreTail tail, StreamRouter router, ReadRequest request, EventFilter filter, int checkpointInterval, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var following = router.Follow(StreamNames.All);
        try
        {
            var head = request.FromCase == ReadRequest.FromOneofCase.End ? await tail.RefreshAsync(cancellationToken).ConfigureAwait(false) : tail.Head;
            var next = request.FromCase switch
            {
                ReadRequest.FromOneofCase.Position => request.Position,
                ReadRequest.FromOneofCase.End => head + 1,
                _ => 0,
            };
            await responseStream.WriteAsync(new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = id, Head = head } }, cancellationToken).ConfigureAwait(false);

            // Positions examined since the last event delivered or checkpoint sent.
            var quiet = 0L;
            while (true)
            {
                while (head > 0 && next <= head)
                {
                    var slice = await store.ScanAllAsync(filter, Direction.Forwards, next, head, ScanWindow, PageSize, cancellationToken).ConfigureAwait(false);
                    foreach (var record in slice.Events)
                    {
                        await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                    }

                    quiet = slice.Events.Count > 0 ? slice.Through - slice.Events[^1].Position : quiet + slice.Through - next + 1;
                    next = slice.Through + 1;
                    if (quiet >= checkpointInterval)
                    {
                        await responseStream.WriteAsync(CheckpointAt(slice.Through, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                        quiet = 0;
                    }
                }

                await responseStream.WriteAsync(CaughtUpAt(head, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);

                while (await following.Events.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var delivered = false;
                    while (following.Events.TryRead(out var record))
                    {
                        if (record.Position < next)
                        {
                            continue;
                        }

                        next = record.Position + 1;
                        head = record.Position;
                        if (filter.Matches(record.Stream, record.Type))
                        {
                            await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                            delivered = true;
                            quiet = 0;
                        }
                        else if (++quiet >= checkpointInterval)
                        {
                            await responseStream.WriteAsync(CheckpointAt(record.Position, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                            quiet = 0;
                        }
                    }

                    if (delivered)
                    {
                        await responseStream.WriteAsync(CaughtUpAt(head, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    }
                }

                following.Dispose();
                following = router.Follow(StreamNames.All);
                head = tail.Head;
            }
        }
        finally
        {
            following.Dispose();
        }
    }

    /// <summary>
    /// A bounded read of a virtual stream: a snapshot up to the refreshed global head, whose bounds
    /// are the stream's own first and last events. A virtual stream with no events reads as empty,
    /// with bounds of zero, never as not found.
    /// </summary>
    private static async Task ReadVirtualBounded(IStreamStore store, IStoreTail tail, VirtualStreamName stream, ReadRequest request, Direction direction, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var head = await tail.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var bounds = await store.VirtualHeadAsync(stream, head, cancellationToken).ConfigureAwait(false);
        await responseStream.WriteAsync(new ReadResponse { Head = new StreamBounds { First = bounds?.First ?? 0, Last = bounds?.Last ?? 0 } }, cancellationToken).ConfigureAwait(false);
        if (bounds is null)
        {
            return;
        }

        var from = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => direction == Direction.Backwards ? bounds.Last : bounds.Last + 1,
            _ => direction == Direction.Forwards ? bounds.First : bounds.Last,
        };

        var remaining = request.Count;
        while (remaining > 0)
        {
            if (direction == Direction.Forwards ? from > bounds.Last : from < bounds.First)
            {
                return;
            }

            var pageSize = (int)Math.Min(remaining, PageSize);
            var page = await store.ReadVirtualAsync(stream, direction, from, head, pageSize, cancellationToken).ConfigureAwait(false);
            foreach (var record in page)
            {
                await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
            }

            remaining -= (ulong)page.Count;
            if (page.Count < pageSize)
            {
                return;
            }

            from = direction == Direction.Forwards ? page[^1].Position + 1 : page[^1].Position - 1;
        }
    }

    /// <summary>
    /// A subscription to a virtual stream: the shape of <c>$all</c>'s, over the predicate, which the
    /// router evaluates through the store too. The head it reports is always the stream's own
    /// last event, so a quiet category keeps a stable head while the store moves on, and an
    /// advance of the store that brings it nothing is silent.
    /// </summary>
    private async Task SubscribeVirtual(IStreamStore store, IStoreTail tail, StreamRouter router, VirtualStreamName stream, ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var following = router.Follow(stream.Name);
        try
        {
            var head = request.FromCase == ReadRequest.FromOneofCase.End ? await tail.RefreshAsync(cancellationToken).ConfigureAwait(false) : tail.Head;
            var bounds = await store.VirtualHeadAsync(stream, head, cancellationToken).ConfigureAwait(false);
            var own = bounds?.Last ?? -1;
            var next = request.FromCase switch
            {
                ReadRequest.FromOneofCase.Position => request.Position,
                ReadRequest.FromOneofCase.End => head + 1,
                _ => 0,
            };
            await responseStream.WriteAsync(new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = id, Head = own } }, cancellationToken).ConfigureAwait(false);

            // The first catch-up always says it is done, even with nothing to deliver; a catch-up
            // after a lapse says so only when it delivered.
            var delivered = true;
            while (true)
            {
                var behind = false;
                while (head > 0 && next <= head)
                {
                    var page = await store.ReadVirtualAsync(stream, Direction.Forwards, next, head, PageSize, cancellationToken).ConfigureAwait(false);
                    if (page.Count == PageSize && !behind)
                    {
                        var rest = await store.CountVirtualAsync(stream, page[^1].Position, head, cancellationToken).ConfigureAwait(false);
                        if (rest > 0)
                        {
                            behind = true;
                            await responseStream.WriteAsync(FellBehindAt(own, rest + page.Count, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                        }
                    }

                    foreach (var record in page)
                    {
                        await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                        own = record.Position;
                        delivered = true;
                    }

                    next = page.Count < PageSize ? head + 1 : page[^1].Position + 1;
                }

                if (delivered)
                {
                    await responseStream.WriteAsync(CaughtUpAt(own, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                }

                while (await following.Events.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    delivered = false;
                    while (following.Events.TryRead(out var record))
                    {
                        if (record.Position < next)
                        {
                            continue;
                        }

                        await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                        own = record.Position;
                        next = record.Position + 1;
                        delivered = true;
                    }

                    if (delivered)
                    {
                        await responseStream.WriteAsync(CaughtUpAt(own, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    }
                }

                following.Dispose();
                following = router.Follow(stream.Name);
                head = tail.Head;
                delivered = false;
            }
        }
        finally
        {
            following.Dispose();
        }
    }

    /// <summary>
    /// A bounded read of a virtual stream by ordinal: a snapshot of what is numbered, whose bounds
    /// are the lowest and highest ordinal assigned. No head is refreshed, because ordinals are
    /// assigned in atomic batches after commit and every assigned one is readable; what the
    /// sequencer has not reached yet is not in the snapshot. A stream with nothing numbered reads
    /// as empty, with bounds of zero.
    /// </summary>
    private static async Task ReadOrdinalBounded(IStreamStore store, VirtualStreamName stream, ReadRequest request, Direction direction, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var bounds = await store.OrdinalHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        await responseStream.WriteAsync(new ReadResponse { Head = new StreamBounds { First = bounds?.First ?? 0, Last = bounds?.Last ?? 0 } }, cancellationToken).ConfigureAwait(false);
        if (bounds is null)
        {
            return;
        }

        var from = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => direction == Direction.Backwards ? bounds.Last : bounds.Last + 1,
            _ => direction == Direction.Forwards ? bounds.First : bounds.Last,
        };

        var remaining = request.Count;
        while (remaining > 0)
        {
            if (direction == Direction.Forwards ? from > bounds.Last : from < bounds.First)
            {
                return;
            }

            var pageSize = (int)Math.Min(remaining, PageSize);
            var page = await store.ReadByOrdinalAsync(stream, direction, from, pageSize, cancellationToken).ConfigureAwait(false);
            foreach (var record in page)
            {
                await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
            }

            remaining -= (ulong)page.Count;
            if (page.Count < pageSize)
            {
                return;
            }

            from = direction == Direction.Forwards ? GetOrdinal(page[^1]) + 1 : GetOrdinal(page[^1]) - 1;
        }
    }

    /// <summary>
    /// A subscription to a virtual stream by ordinal. Every number is an ordinal: the confirmation
    /// carries the highest one assigned, or -1 when none is, and from the end means after that.
    /// The catch-up drains the numbered rows; the live phase waits on the tail like every other
    /// subscription, then looks for newly numbered rows, and while the sequencer is still behind
    /// the head it woke for, waits for the numbering to reach it rather than for an advance that
    /// a quiet store would never bring. A store advance that brings this stream nothing is
    /// silent, as under global numbering.
    /// </summary>
    private async Task SubscribeOrdinal(IStreamStore store, IStoreTail tail, VirtualStreamName stream, ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var observed = tail.Head;
        var bounds = await store.OrdinalHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        var own = bounds?.Last ?? -1;
        var next = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => own + 1,
            _ => 0,
        };
        await responseStream.WriteAsync(new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = id, Head = own } }, cancellationToken).ConfigureAwait(false);

        var delivered = true;
        while (true)
        {
            var behind = false;
            var numbered = tail.Numbered;
            while (true)
            {
                var page = await store.ReadByOrdinalAsync(stream, Direction.Forwards, next, PageSize, cancellationToken).ConfigureAwait(false);
                if (page.Count == 0)
                {
                    break;
                }

                if (page.Count == PageSize && !behind)
                {
                    // Ordinals are dense, so the distance to the highest assigned one is the count,
                    // holes included.
                    var head = (await store.OrdinalHeadAsync(stream, cancellationToken).ConfigureAwait(false))?.Last ?? own;
                    if (head > GetOrdinal(page[^1]))
                    {
                        behind = true;
                        await responseStream.WriteAsync(FellBehindAt(head, head - next + 1, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    }
                }

                foreach (var record in page)
                {
                    await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                    own = GetOrdinal(record);
                    delivered = true;
                }

                next = own + 1;
                if (page.Count < PageSize)
                {
                    break;
                }
            }

            if (delivered)
            {
                await responseStream.WriteAsync(CaughtUpAt(own, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                delivered = false;
            }

            // If everything up to the observed head was numbered before the read began, the read
            // saw it all and the next thing to wait for is an advance; otherwise the sequencer
            // is behind, and the next look follows its progress.
            observed = await EventSource.AwaitNumberingAsync(tail, numbered, observed, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long GetOrdinal(EventRecord record) =>
        record.Ordinal ?? throw new InvalidOperationException("The store returned an event without its ordinal from an ordinal read.");

    /// <summary>One page of a plain stream; a bounded read accepts a view that may lag, a subscription does not.</summary>
    private static async Task<StreamSlice?> ReadPage(IStreamStore store, string stream, Direction direction, long? from, int count, CancellationToken cancellationToken, bool eventual = false)
    {
        try
        {
            return eventual
                ? await store.ReadEventualAsync(stream, direction, from, count, cancellationToken).ConfigureAwait(false)
                : await store.ReadAsync(stream, direction, from, count, cancellationToken).ConfigureAwait(false);
        }
        catch (StreamDeletedException deleted)
        {
            throw NightingaleErrors.StreamDeleted(deleted.Stream);
        }
    }
}
