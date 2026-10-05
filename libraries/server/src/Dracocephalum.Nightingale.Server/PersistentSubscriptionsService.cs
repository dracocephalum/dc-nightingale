using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The <c>PersistentSubscriptions</c> service over the group store and the stream store. Create,
/// delete, list and replay go straight to the group store; a group's info does too, with what
/// the running group adds when it runs here. A read takes the group's lease for this
/// instance, refuses a second consumer, runs the group in memory for as long as the consumer
/// stays, and turns the consumer's acknowledgements into the group's checkpoint.
/// </summary>
/// <param name="groups">The group store.</param>
/// <param name="store">The stream store.</param>
/// <param name="tail">The tail.</param>
/// <param name="registry">The groups live in this instance.</param>
/// <param name="address">Where this instance is reached, written with every lease it takes.</param>
/// <param name="timeProvider">The clock.</param>
public sealed class PersistentSubscriptionsService(ISubscriptionGroupStore groups, IStreamStore store, IStoreTail tail, SubscriptionGroupRegistry registry, InstanceAddress address, TimeProvider timeProvider) : PersistentSubscriptions.PersistentSubscriptionsBase
{
    /// <summary>How long a lease lasts; it is renewed at a third of this.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>The most ids one acknowledgement may carry.</summary>
    public const int MaxIdsPerAck = 2000;

    /// <inheritdoc/>
    public override async Task<CreateResponse> Create(CreateRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var stream = ReadableStreamName(request.Stream);
        var group = GroupName(request.Group);
        GroupSettings settings;
        try
        {
            settings = request.Settings is null ? GroupSettings.Default : request.Settings.ToGroupSettings();
            settings.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw NightingaleErrors.InvalidArgument($"Group settings: {exception.ParamName} is out of range.");
        }

        // The numbering is the group's for life, so it is checked once, here, the way a read checks it.
        if (settings.Numbering == Numbering.Ordinal)
        {
            if (!StreamNames.TryParseVirtual(stream, out _))
            {
                throw NightingaleErrors.InvalidArgument("Ordinal numbering applies to $ce- and $et- streams only.");
            }

            if (!store.OrdinalsEnabled)
            {
                throw NightingaleErrors.OrdinalsNotEnabled(stream);
            }
        }

        try
        {
            await groups.CreateAsync(new SubscriptionGroupDefinition(stream, group, settings, -1), context.CancellationToken).ConfigureAwait(false);
        }
        catch (GroupExistsException)
        {
            throw NightingaleErrors.GroupExists(stream, group);
        }
        catch (ValueTooLongException tooLong)
        {
            throw NightingaleErrors.InvalidArgument(tooLong.Message);
        }

        return new CreateResponse();
    }

    /// <inheritdoc/>
    public override async Task<DeleteGroupResponse> Delete(DeleteGroupRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var stream = ReadableStreamName(request.Stream);
        var group = GroupName(request.Group);
        if (!await groups.DeleteAsync(stream, group, context.CancellationToken).ConfigureAwait(false))
        {
            throw NightingaleErrors.GroupNotFound(stream, group);
        }

        return new DeleteGroupResponse();
    }

    /// <inheritdoc/>
    public override async Task<GetInfoResponse> GetInfo(GetInfoRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var stream = ReadableStreamName(request.Stream);
        var group = GroupName(request.Group);
        var summary = await groups.DescribeAsync(stream, group, context.CancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.GroupNotFound(stream, group);
        var definition = summary.Definition;

        // What a running group has outstanding is known only where it runs, so the question goes
        // there: refused with the owner's address, and the client repeats it there. An owner that
        // advertises no address cannot be gone to, and a group nobody runs has nothing more to
        // say, so both are answered here with what the store holds.
        var here = summary.Holder is { } holder && string.Equals(holder.Owner, registry.InstanceId, StringComparison.Ordinal);
        if (!here && summary.Holder is { Address: not null } elsewhere)
        {
            throw NightingaleErrors.GroupOwnedElsewhere(definition.Stream, definition.Group, elsewhere);
        }

        var info = Info(summary);
        if (await LastKnownAsync(definition, context.CancellationToken).ConfigureAwait(false) is { } last)
        {
            info.LastKnownPosition = last;
        }

        // Asked where the group runs, it says how it stands now; asked elsewhere with nowhere to
        // send the client, what it last wrote to its row is the answer, already in the info.
        if (here && registry.Describe(definition.Id) is { } live)
        {
            info.Live = Live(live, fromOwner: true);
        }

        return new GetInfoResponse { Info = info };
    }

    /// <inheritdoc/>
    public override async Task<ListResponse> List(ListRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var stream = request.Stream.Length == 0 ? null : ReadableStreamName(request.Stream);
        var response = new ListResponse();
        foreach (var summary in await groups.ListAsync(stream, context.CancellationToken).ConfigureAwait(false))
        {
            // A group this instance runs is asked directly, which costs nothing and is as of
            // now; the others are as their rows have them.
            var info = Info(summary);
            if (summary.Holder is { } holder
                && string.Equals(holder.Owner, registry.InstanceId, StringComparison.Ordinal)
                && registry.Describe(summary.Definition.Id) is { } live)
            {
                info.Live = Live(live, fromOwner: true);
            }

            response.Groups.Add(info);
        }

        return response;
    }

    /// <inheritdoc/>
    public override async Task<ReplayParkedResponse> ReplayParked(ReplayParkedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var stream = ReadableStreamName(request.Stream);
        var group = GroupName(request.Group);
        var definition = await groups.GetAsync(stream, group, context.CancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.GroupNotFound(stream, group);

        // Which names are the same group is the store's to say, under whatever collation it has:
        // a case-insensitive database answers "Billing" with the group created as "billing". From
        // here on the group is its id, which the registry, the lease, the parked rows and the
        // outbox go by, and its names are the ones in its own row.
        stream = definition.Stream;
        group = definition.Group;

        // A running group's consumer is woken by the instance that runs it, so a replay goes
        // there: refused with the owner's address, and the client repeats it there. With no
        // consumer anywhere, the move is done here and delivered at the next connection.
        var holder = await groups.LeaseHolderAsync(SubscriptionGroupRegistry.LeaseName(definition.Id), context.CancellationToken).ConfigureAwait(false);
        if (holder is not null && !string.Equals(holder.Owner, registry.InstanceId, StringComparison.Ordinal))
        {
            throw NightingaleErrors.GroupOwnedElsewhere(stream, group, holder);
        }

        // The caller's number is in the group's numbering: a revision for a plain stream, a
        // position for $all or a virtual stream, an ordinal for a group created under it.
        var by = definition.Settings.Numbering == Numbering.Ordinal ? SubscriptionParkedNumber.Ordinal
            : StreamNames.IsReserved(stream) ? SubscriptionParkedNumber.Position
            : SubscriptionParkedNumber.Revision;
        long? position = request.WhichCase == ReplayParkedRequest.WhichOneofCase.Position ? request.Position : null;
        var replayed = await groups.ReplayAsync(definition.Id, position, by, timeProvider.GetUtcNow(), context.CancellationToken).ConfigureAwait(false);
        if (position is { } wanted && replayed == 0)
        {
            throw NightingaleErrors.ParkedMessageNotFound(stream, group, wanted);
        }

        // A consumer connected here gets the outbox at once; one connected elsewhere, or none,
        // gets it at its next connection.
        registry.Wake(definition.Id);
        return new ReplayParkedResponse { Replayed = replayed };
    }

    /// <inheritdoc/>
    public override async Task Read(IAsyncStreamReader<PersistentReadRequest> requestStream, IServerStreamWriter<PersistentReadResponse> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var cancellationToken = context.CancellationToken;
        if (!await requestStream.MoveNext(cancellationToken).ConfigureAwait(false)
            || requestStream.Current.ContentCase != PersistentReadRequest.ContentOneofCase.Options)
        {
            throw NightingaleErrors.InvalidArgument("The first message of a persistent read carries the options.");
        }

        var options = requestStream.Current.Options;
        var stream = ReadableStreamName(options.Stream);
        var group = GroupName(options.Group);
        var buffer = options.BufferSize > 0 ? options.BufferSize : 10;
        var definition = await groups.GetAsync(stream, group, cancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.GroupNotFound(stream, group);

        // Which names are the same group is the store's to say, under whatever collation it has:
        // a case-insensitive database answers "Billing" with the group created as "billing". From
        // here on the group is its id, which the registry, the lease, the parked rows and the
        // outbox go by, and its names are the ones in its own row.
        stream = definition.Stream;
        group = definition.Group;

        if (!registry.TryClaim(definition.Id))
        {
            throw NightingaleErrors.ConsumerLimitReached(stream, group, definition.Settings.MaxSubscriberCount);
        }

        try
        {
            // Refused with the owner's address: the client goes there itself, the way the
            // reference client follows a not-leader answer.
            var lease = SubscriptionGroupRegistry.LeaseName(definition.Id);
            var owner = await groups.AcquireLeaseAsync(lease, registry.InstanceId, address.Current, LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (owner is not null)
            {
                throw NightingaleErrors.GroupOwnedElsewhere(stream, group, owner);
            }

            try
            {
                await ServeAsync(definition, buffer, lease, context.Peer, requestStream, responseStream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Cleared before the lease goes, so the row never says a consumer is connected
                // under no lease; an instance that dies first leaves it, and readers go by the lease.
                await groups.SaveLiveAsync(definition.Id, null, CancellationToken.None).ConfigureAwait(false);
                await groups.ReleaseLeaseAsync(lease, registry.InstanceId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            registry.Release(definition.Id);
        }
    }

    /// <summary>What the store holds about a group, in its wire form.</summary>
    private static GroupInfo Info(SubscriptionGroupSummary summary)
    {
        var definition = summary.Definition;
        var info = new GroupInfo
        {
            Stream = definition.Stream,
            Group = definition.Group,
            Settings = definition.Settings.ToWire(),
            CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(summary.CreatedAt),
            ParkedCount = summary.ParkedCount,
            OutboxCount = summary.OutboxCount,
            Running = summary.Holder is not null,
            OwnerAddress = summary.Holder?.Address?.ToString() ?? string.Empty,
            Live = summary.Live is null ? null : Live(summary.Live, fromOwner: false),
        };
        if (definition.Checkpoint >= 0)
        {
            info.Checkpoint = definition.Checkpoint;
        }

        return info;
    }

    /// <summary>How a running group stands, in its wire form.</summary>
    private static GroupLiveInfo Live(SubscriptionGroupLive live, bool fromOwner)
    {
        var info = new GroupLiveInfo
        {
            ConnectedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(live.ConnectedAt),
            InFlightCount = live.InFlightCount,
            AwaitingRetryCount = live.AwaitingRetryCount,
            ConsumerBufferSize = live.ConsumerBufferSize,
            ConsumerAddress = live.ConsumerAddress ?? string.Empty,
            AsOf = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(live.AsOf),
            FromOwner = fromOwner,
        };
        if (live.Checkpoint is { } checkpoint)
        {
            info.Checkpoint = checkpoint;
        }

        if (live.OldestInFlightAt is { } oldest)
        {
            info.OldestInFlightAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(oldest);
        }

        return info;
    }

    /// <summary>
    /// The last number of the group's stream, in the group's numbering: an ordinal under ordinal
    /// numbering, a position for <c>$all</c> or a virtual stream, a revision for a plain stream.
    /// </summary>
    private async Task<long?> LastKnownAsync(SubscriptionGroupDefinition definition, CancellationToken cancellationToken)
    {
        if (StreamNames.TryParseVirtual(definition.Stream, out var virtualStream))
        {
            var head = definition.Settings.Numbering == Numbering.Ordinal
                ? await store.OrdinalHeadAsync(virtualStream, cancellationToken).ConfigureAwait(false)
                : await store.VirtualHeadAsync(virtualStream, tail.Head, cancellationToken).ConfigureAwait(false);
            return head?.Last;
        }

        if (definition.Stream == StreamNames.All)
        {
            return tail.Head > 0 ? tail.Head : null;
        }

        try
        {
            return (await store.ReadAsync(definition.Stream, Direction.Backwards, null, 1, cancellationToken).ConfigureAwait(false))?.Head.Last;
        }
        catch (StreamDeletedException)
        {
            // A group outlives its stream; a deleted stream has no last number to report.
            return null;
        }
    }

    private static string ReadableStreamName(string stream)
    {
        if (string.IsNullOrEmpty(stream))
        {
            throw NightingaleErrors.InvalidStreamName(stream, "empty");
        }

        if (stream.Length > StreamsService.MaxStreamNameLength)
        {
            throw NightingaleErrors.InvalidStreamName(stream, $"longer than {StreamsService.MaxStreamNameLength} characters");
        }

        if (StreamNames.IsReserved(stream) && stream != StreamNames.All && !StreamNames.TryParseVirtual(stream, out _))
        {
            throw NightingaleErrors.InvalidStreamName(stream, "reserved and not $all, $ce-<category> or $et-<event type>");
        }

        return stream;
    }

    private static string GroupName(string group)
    {
        if (string.IsNullOrWhiteSpace(group) || group.Length > 250)
        {
            throw NightingaleErrors.InvalidArgument("A group name is 1 to 250 characters.");
        }

        return group;
    }

    private static List<Guid> Ids(IEnumerable<string> ids)
    {
        var parsed = new List<Guid>();
        foreach (var id in ids)
        {
            if (!Guid.TryParseExact(id, "D", out var guid))
            {
                throw NightingaleErrors.InvalidArgument("An event id is a UUID in its canonical form.");
            }

            parsed.Add(guid);
        }

        if (parsed.Count > MaxIdsPerAck)
        {
            throw NightingaleErrors.InvalidArgument($"At most {MaxIdsPerAck} ids per acknowledgement.");
        }

        return parsed;
    }

    private async Task ServeAsync(SubscriptionGroupDefinition definition, int buffer, string lease, string? peer, IAsyncStreamReader<PersistentReadRequest> requestStream, IServerStreamWriter<PersistentReadResponse> responseStream, CancellationToken cancellationToken)
    {
        await using var live = new SubscriptionGroupRuntime(store, tail, groups, timeProvider, definition, buffer);
        SubscriptionGroupLive Describe() => live.Describe() with { ConsumerAddress = peer };
        registry.Attach(definition.Id, live.Wake, Describe);

        // Written once now, so a listing shows the consumer as soon as it is connected, and
        // again with every renewal of the lease.
        await groups.SaveLiveAsync(definition.Id, Describe(), cancellationToken).ConfigureAwait(false);
        await responseStream.WriteAsync(
            new PersistentReadResponse { Confirmed = new PersistentSubscriptionConfirmed { SubscriptionId = Guid.NewGuid().ToString("D"), Checkpoint = live.Checkpoint } },
            cancellationToken).ConfigureAwait(false);

        using var ending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = ending.Token;
        var sending = SendAsync(live, responseStream, token);
        var receiving = ReceiveAsync(live, requestStream, token);
        var keeping = KeepAsync(live, definition.Id, Describe, lease, definition.Settings.MessageTimeout, token);

        var first = await Task.WhenAny(sending, receiving, keeping, live.Delivery).ConfigureAwait(false);
        await ending.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(sending, receiving, keeping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ending the call cancels the others; the first task's outcome is what matters.
        }

        if (first != live.Delivery || first.IsFaulted)
        {
            await first.ConfigureAwait(false);
        }
    }

    private static async Task SendAsync(SubscriptionGroupRuntime live, IServerStreamWriter<PersistentReadResponse> responseStream, CancellationToken cancellationToken)
    {
        await foreach (var message in live.Outgoing.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(
                new PersistentReadResponse { Event = new PersistentEvent { Event = message.Record.ToRecordedEvent(), RetryCount = message.RetryCount } },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveAsync(SubscriptionGroupRuntime live, IAsyncStreamReader<PersistentReadRequest> requestStream, CancellationToken cancellationToken)
    {
        while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            switch (requestStream.Current.ContentCase)
            {
                case PersistentReadRequest.ContentOneofCase.Ack:
                    await live.AcknowledgeAsync(Ids(requestStream.Current.Ack.Ids)).ConfigureAwait(false);
                    break;
                case PersistentReadRequest.ContentOneofCase.Nack:
                    var nack = requestStream.Current.Nack;
                    var action = nack.Action switch
                    {
                        Protocol.V1.NackAction.Park => Nightingale.NackAction.Park,
                        Protocol.V1.NackAction.Skip => Nightingale.NackAction.Skip,
                        _ => Nightingale.NackAction.Retry,
                    };
                    await live.RefuseAsync(Ids(nack.Ids), action, nack.Reason).ConfigureAwait(false);
                    break;
                default:
                    throw NightingaleErrors.InvalidArgument("After the options, every message of a persistent read acknowledges or refuses events.");
            }
        }
    }

    /// <summary>Renews the lease, redelivers what timed out and writes how the group stands, on a cadence tied to the message timeout.</summary>
    private async Task KeepAsync(SubscriptionGroupRuntime live, Guid groupId, Func<SubscriptionGroupLive> describe, string lease, TimeSpan messageTimeout, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromTicks(Math.Min(messageTimeout.Ticks / 2, LeaseDuration.Ticks / 3));
        while (true)
        {
            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
            var owner = await groups.AcquireLeaseAsync(lease, registry.InstanceId, address.Current, LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (owner is not null)
            {
                throw NightingaleErrors.GroupOwnedElsewhere(live.ToString() ?? string.Empty, lease, owner);
            }

            await live.ExpireAsync().ConfigureAwait(false);
            await groups.SaveLiveAsync(groupId, describe(), cancellationToken).ConfigureAwait(false);
        }
    }
}
