using System.Runtime.ExceptionServices;
using System.Threading.Channels;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Dracocephalum.Nightingale.Server.Auth;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The <c>PersistentSubscriptions</c> service over the group store and the stream store. Create,
/// update, delete, list and replay go straight to the group store; a group's info does too, with what
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

    /// <summary>How many parked or outbox messages a listing returns when the caller names no limit.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>The most parked or outbox messages one listing returns.</summary>
    public const int MaxPageSize = 1000;

    /// <inheritdoc/>
    public override async Task<CreateResponse> Create(CreateRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false);

        var stream = RequireReadableStreamName(request.Stream);
        var group = RequireGroupName(request.Group);
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
    public override async Task<UpdateResponse> Update(UpdateRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false);

        var definition = await GroupAsync(request.Stream, request.Group, context.CancellationToken).ConfigureAwait(false);

        // A running group has its settings in memory and a consumer delivered to under them, so
        // the change is made where it runs: refused with the owner's address, and the client
        // repeats it there, where the consumer's call can be ended.
        var holder = await groups.LeaseHolderAsync(SubscriptionGroupRegistry.GetLeaseName(definition.Id), context.CancellationToken).ConfigureAwait(false);
        if (holder is not null && !string.Equals(holder.Owner, registry.InstanceId, StringComparison.Ordinal))
        {
            throw NightingaleErrors.GroupOwnedElsewhere(definition.Stream, definition.Group, holder);
        }

        // Where a group starts and how it counts say what its checkpoint means; they are not
        // changed under one. A field the caller left unset keeps its value.
        var current = definition.Settings;
        var asked = request.Settings ?? new Protocol.V1.GroupSettings();
        var start = asked.StartCase switch
        {
            Protocol.V1.GroupSettings.StartOneofCase.FromStart => StreamPosition.Start,
            Protocol.V1.GroupSettings.StartOneofCase.FromEnd => StreamPosition.End,
            Protocol.V1.GroupSettings.StartOneofCase.FromPosition => StreamPosition.From(asked.FromPosition),
            _ => current.Start,
        };
        if (start != current.Start)
        {
            throw NightingaleErrors.InvalidArgument("Where a group starts is fixed when it is created; delete the group and create it again to start elsewhere.");
        }

        if (asked.Numbering != Protocol.V1.Numbering.Unspecified && asked.Numbering.ToNumbering() != current.Numbering)
        {
            throw NightingaleErrors.InvalidArgument("A group's numbering is fixed when it is created; a consumer that wants the other numbering creates another group.");
        }

        var settings = current with
        {
            MessageTimeout = asked.MessageTimeout?.ToTimeSpan() ?? current.MessageTimeout,
            MaxRetryCount = asked.MaxRetryCount > 0 ? asked.MaxRetryCount : current.MaxRetryCount,
            CheckpointUpperBound = asked.CheckpointUpperBound > 0 ? asked.CheckpointUpperBound : current.CheckpointUpperBound,
            CheckpointAfter = asked.CheckpointAfter?.ToTimeSpan() ?? current.CheckpointAfter,
            CheckpointLowerBound = asked.CheckpointLowerBound > 0 ? asked.CheckpointLowerBound : current.CheckpointLowerBound,
            BufferSize = asked.BufferSize > 0 ? asked.BufferSize : current.BufferSize,
            MaxSubscriberCount = asked.MaxSubscriberCount > 0 ? asked.MaxSubscriberCount : current.MaxSubscriberCount,
            ConsumerStrategy = asked.ConsumerStrategy == Protocol.V1.ConsumerStrategy.Unspecified ? current.ConsumerStrategy : asked.ConsumerStrategy.ToConsumerStrategy(),
        };
        try
        {
            settings.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw NightingaleErrors.InvalidArgument($"Group settings: {exception.ParamName} is out of range.");
        }

        if (!await groups.UpdateSettingsAsync(definition.Id, settings, context.CancellationToken).ConfigureAwait(false))
        {
            throw NightingaleErrors.GroupNotFound(definition.Stream, definition.Group);
        }

        // The consumers connected here were delivered to under the old settings; their calls
        // end and they connect again under the new ones.
        registry.Stop(definition.Id, NightingaleErrors.GroupUpdated(definition.Stream, definition.Group));
        return new UpdateResponse { Settings = settings.ToWire() };
    }

    /// <inheritdoc/>
    public override async Task<DeleteGroupResponse> Delete(DeleteGroupRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false);

        var stream = RequireReadableStreamName(request.Stream);
        var group = RequireGroupName(request.Group);
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
        await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false);

        var stream = RequireReadableStreamName(request.Stream);
        var group = RequireGroupName(request.Group);
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

        var info = ToInfo(summary);
        if (await LastKnownAsync(definition, context.CancellationToken).ConfigureAwait(false) is { } last)
        {
            info.LastKnownPosition = last;
        }

        // Asked where the group runs, it says how it stands now; asked elsewhere with nowhere to
        // send the client, what it last wrote to its row is the answer, already in the info.
        if (here && registry.Describe(definition.Id) is { } live)
        {
            info.Live = ToLiveInfo(live, fromOwner: true);
        }

        return new GetInfoResponse { Info = info };
    }

    /// <inheritdoc/>
    public override async Task<ListResponse> List(ListRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false);

        var stream = request.Stream.Length == 0 ? null : RequireReadableStreamName(request.Stream);
        var response = new ListResponse();
        foreach (var summary in await groups.ListAsync(stream, context.CancellationToken).ConfigureAwait(false))
        {
            // A group this instance runs is asked directly, which costs nothing and is as of
            // now; the others are as their rows have them.
            var info = ToInfo(summary);
            if (summary.Holder is { } holder
                && string.Equals(holder.Owner, registry.InstanceId, StringComparison.Ordinal)
                && registry.Describe(summary.Definition.Id) is { } live)
            {
                info.Live = ToLiveInfo(live, fromOwner: true);
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
        await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false);

        var stream = RequireReadableStreamName(request.Stream);
        var group = RequireGroupName(request.Group);
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
        var holder = await groups.LeaseHolderAsync(SubscriptionGroupRegistry.GetLeaseName(definition.Id), context.CancellationToken).ConfigureAwait(false);
        if (holder is not null && !string.Equals(holder.Owner, registry.InstanceId, StringComparison.Ordinal))
        {
            throw NightingaleErrors.GroupOwnedElsewhere(stream, group, holder);
        }

        var by = GetNumberKind(definition);
        long? position = request.WhichCase == ReplayParkedRequest.WhichOneofCase.Position ? request.Position : null;
        var replayed = request.WhichCase == ReplayParkedRequest.WhichOneofCase.Before
            ? await groups.ReplayBeforeAsync(definition.Id, request.Before, by, timeProvider.GetUtcNow(), context.CancellationToken).ConfigureAwait(false)
            : await groups.ReplayAsync(definition.Id, position, by, timeProvider.GetUtcNow(), context.CancellationToken).ConfigureAwait(false);
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
    public override async Task<ListParkedResponse> ListParked(ListMessagesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false);

        var definition = await GroupAsync(request.Stream, request.Group, context.CancellationToken).ConfigureAwait(false);
        var by = GetNumberKind(definition);
        var response = new ListParkedResponse();
        foreach (var message in await groups.ListParkedAsync(definition.Id, by, request.HasAfter ? request.After : null, ClampLimit(request.Limit), context.CancellationToken).ConfigureAwait(false))
        {
            response.Messages.Add(new ParkedMessage
            {
                Number = PickNumber(by, message.Position, message.Revision, message.Ordinal),
                EventId = message.EventId.ToString("D"),
                Reason = message.Reason,
                RetryCount = message.Attempts,
                ParkedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(message.ParkedAt),
                Position = message.Position,
                Revision = message.Revision,
            });
        }

        return response;
    }

    /// <inheritdoc/>
    public override async Task<ListOutboxResponse> ListOutbox(ListMessagesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false);

        var definition = await GroupAsync(request.Stream, request.Group, context.CancellationToken).ConfigureAwait(false);
        var by = GetNumberKind(definition);
        var response = new ListOutboxResponse();
        foreach (var message in await groups.ListOutboxAsync(definition.Id, by, request.HasAfter ? request.After : null, ClampLimit(request.Limit), context.CancellationToken).ConfigureAwait(false))
        {
            response.Messages.Add(new OutboxMessage
            {
                Number = PickNumber(by, message.Position, message.Revision, message.Ordinal),
                EventId = message.EventId.ToString("D"),
                Reason = message.Reason,
                RetryCount = message.Attempts,
                DueAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(message.DueAt),
                Position = message.Position,
                Revision = message.Revision,
            });
        }

        return response;
    }

    /// <inheritdoc/>
    public override async Task<SkipParkedResponse> SkipParked(SkipParkedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await context.AuthorizeAsync(CredentialRole.Ops, TenantAccess.Write).ConfigureAwait(false);

        // Parked messages are rows and no running group holds them in memory, so any instance
        // removes them; there is no consumer to wake and nowhere to send the caller.
        var definition = await GroupAsync(request.Stream, request.Group, context.CancellationToken).ConfigureAwait(false);
        long? position = request.WhichCase == SkipParkedRequest.WhichOneofCase.Position ? request.Position : null;
        long? before = request.WhichCase == SkipParkedRequest.WhichOneofCase.Before ? request.Before : null;
        var skipped = await groups.SkipAsync(definition.Id, position, before, GetNumberKind(definition), context.CancellationToken).ConfigureAwait(false);
        if (position is { } wanted && skipped == 0)
        {
            throw NightingaleErrors.ParkedMessageNotFound(definition.Stream, definition.Group, wanted);
        }

        return new SkipParkedResponse { Skipped = skipped };
    }

    /// <inheritdoc/>
    public override async Task Read(IAsyncStreamReader<PersistentReadRequest> requestStream, IServerStreamWriter<PersistentReadResponse> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        await context.AuthorizeAsync(CredentialRole.User, TenantAccess.Write).ConfigureAwait(false);

        var cancellationToken = context.CancellationToken;
        if (!await requestStream.MoveNext(cancellationToken).ConfigureAwait(false)
            || requestStream.Current.ContentCase != PersistentReadRequest.ContentOneofCase.Options)
        {
            throw NightingaleErrors.InvalidArgument("The first message of a persistent read carries the options.");
        }

        var options = requestStream.Current.Options;
        var stream = RequireReadableStreamName(options.Stream);
        var group = RequireGroupName(options.Group);
        var buffer = options.BufferSize > 0 ? options.BufferSize : 10;
        var definition = await groups.GetAsync(stream, group, cancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.GroupNotFound(stream, group);

        // Which names are the same group is the store's to say, under whatever collation it has:
        // a case-insensitive database answers "Billing" with the group created as "billing". From
        // here on the group is its id, which the registry, the lease, the parked rows and the
        // outbox go by, and its names are the ones in its own row.
        stream = definition.Stream;
        group = definition.Group;

        // The group runs once in this instance, shared by its consumers here: the first to
        // arrive starts it, the others join it, the last to leave stops it.
        var seat = await registry.JoinAsync(definition.Id, definition.Settings.MaxSubscriberCount, cancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.ConsumerLimitReached(stream, group, definition.Settings.MaxSubscriberCount);
        try
        {
            SubscriptionGroupHost host;
            if (seat.First)
            {
                try
                {
                    host = await StartAsync(definition, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception cause)
                {
                    seat.Failed(cause);
                    throw;
                }

                seat.Started(host);
            }
            else
            {
                host = await seat.Host.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await ServeAsync(host.Runtime, buffer, context.Peer, requestStream, responseStream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (registry.Leave(seat))
            {
                try
                {
                    if (seat.Host.IsCompletedSuccessfully)
                    {
                        await StopAsync(seat.Host.Result).ConfigureAwait(false);
                    }
                }
                finally
                {
                    registry.Closed(seat);
                }
            }
        }
    }

    /// <summary>Starts a group in this instance, under its lease.</summary>
    private async Task<SubscriptionGroupHost> StartAsync(SubscriptionGroupDefinition definition, CancellationToken cancellationToken)
    {
        // Refused with the owner's address: the client goes there itself, the way the reference
        // client follows a not-leader answer.
        var lease = SubscriptionGroupRegistry.GetLeaseName(definition.Id);
        var owner = await groups.AcquireLeaseAsync(lease, registry.InstanceId, address.Current, LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (owner is not null)
        {
            throw NightingaleErrors.GroupOwnedElsewhere(definition.Stream, definition.Group, owner);
        }

        var runtime = new SubscriptionGroupRuntime(store, tail, groups, timeProvider, definition);
        return new SubscriptionGroupHost(definition, runtime, (live, token) => KeepAsync(live, definition, lease, token));
    }

    /// <summary>Stops a group in this instance: its last consumer has left.</summary>
    private async Task StopAsync(SubscriptionGroupHost host)
    {
        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Cleared before the lease goes, so the row never says a consumer is connected under
            // no lease; an instance that dies first leaves it, and readers go by the lease.
            await groups.SaveLiveAsync(host.Definition.Id, null, CancellationToken.None).ConfigureAwait(false);
            await groups.ReleaseLeaseAsync(SubscriptionGroupRegistry.GetLeaseName(host.Definition.Id), registry.InstanceId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The number a group's caller addresses a message by: a revision for a group over a plain
    /// stream, a position over <c>$all</c> or a virtual stream, an ordinal for a group created
    /// under ordinal numbering.
    /// </summary>
    private static SubscriptionParkedNumber GetNumberKind(SubscriptionGroupDefinition definition) =>
        definition.Settings.Numbering == Numbering.Ordinal ? SubscriptionParkedNumber.Ordinal
        : StreamNames.IsReserved(definition.Stream) ? SubscriptionParkedNumber.Position
        : SubscriptionParkedNumber.Revision;

    private static long PickNumber(SubscriptionParkedNumber by, long position, long revision, long? ordinal) => by switch
    {
        SubscriptionParkedNumber.Revision => revision,
        SubscriptionParkedNumber.Ordinal => ordinal ?? -1,
        _ => position,
    };

    private static int ClampLimit(int asked) => asked switch
    {
        <= 0 => DefaultPageSize,
        > MaxPageSize => throw NightingaleErrors.InvalidArgument($"A page holds at most {MaxPageSize} messages."),
        _ => asked,
    };

    /// <summary>The group two names find, known from here on by the names in its own row.</summary>
    private async Task<SubscriptionGroupDefinition> GroupAsync(string streamName, string groupName, CancellationToken cancellationToken)
    {
        var stream = RequireReadableStreamName(streamName);
        var group = RequireGroupName(groupName);
        return await groups.GetAsync(stream, group, cancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.GroupNotFound(stream, group);
    }

    /// <summary>What the store holds about a group, in its wire form.</summary>
    private static GroupInfo ToInfo(SubscriptionGroupSummary summary)
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
            Live = summary.Live is null ? null : ToLiveInfo(summary.Live, fromOwner: false),
        };
        if (definition.Checkpoint >= 0)
        {
            info.Checkpoint = definition.Checkpoint;
        }

        return info;
    }

    /// <summary>How a running group stands, in its wire form.</summary>
    private static GroupLiveInfo ToLiveInfo(SubscriptionGroupLive live, bool fromOwner)
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
            ConsumerCount = live.ConsumerCount,
        };
        foreach (var consumer in live.Consumers)
        {
            info.Consumers.Add(new ConsumerInfo
            {
                ConnectedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(consumer.ConnectedAt),
                Address = consumer.Address ?? string.Empty,
                BufferSize = consumer.BufferSize,
                InFlightCount = consumer.InFlightCount,
            });
        }

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

    private static string RequireReadableStreamName(string stream)
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

    private static string RequireGroupName(string group)
    {
        if (string.IsNullOrWhiteSpace(group) || group.Length > 250)
        {
            throw NightingaleErrors.InvalidArgument("A group name is 1 to 250 characters.");
        }

        return group;
    }

    private static List<Guid> ParseIds(IEnumerable<string> ids)
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

    /// <summary>
    /// Serves one consumer of a running group: its events go out as the group hands them over,
    /// what it acknowledges or refuses goes back, and the call ends when the consumer completes
    /// its requests, a clean leave, when it goes away, or when the group has no more for it,
    /// with the cause.
    /// </summary>
    private async Task ServeAsync(SubscriptionGroupRuntime live, int buffer, string? peer, IAsyncStreamReader<PersistentReadRequest> requestStream, IServerStreamWriter<PersistentReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var consumer = live.Join(buffer, peer);
        try
        {
            // Written now, so a listing shows the consumer as soon as it is connected, and again
            // with every renewal of the lease.
            await groups.SaveLiveAsync(live.Definition.Id, live.Describe(), cancellationToken).ConfigureAwait(false);
            await responseStream.WriteAsync(
                new PersistentReadResponse { Confirmed = new PersistentSubscriptionConfirmed { SubscriptionId = consumer.Id.ToString("D"), Checkpoint = live.Checkpoint } },
                cancellationToken).ConfigureAwait(false);

            using var ending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = ending.Token;
            var sending = SendAsync(consumer, responseStream, token);
            var receiving = ReceiveAsync(live, requestStream, token);

            var first = await Task.WhenAny(sending, receiving).ConfigureAwait(false);
            await ending.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(sending, receiving).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ending the call cancels the other; the first task's outcome is what matters.
            }

            await first.ConfigureAwait(false);
        }
        finally
        {
            live.Leave(consumer);
        }
    }

    private static async Task SendAsync(SubscriptionConsumer consumer, IServerStreamWriter<PersistentReadResponse> responseStream, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in consumer.Outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(
                    new PersistentReadResponse { Event = new PersistentEvent { Event = message.Record.ToRecordedEvent(), RetryCount = message.RetryCount } },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException closed) when (closed.InnerException is { } cause)
        {
            // The group completed the channel with a cause; the call ends with that, not the wrapper.
            ExceptionDispatchInfo.Throw(cause);
        }
    }

    private static async Task ReceiveAsync(SubscriptionGroupRuntime live, IAsyncStreamReader<PersistentReadRequest> requestStream, CancellationToken cancellationToken)
    {
        while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            switch (requestStream.Current.ContentCase)
            {
                case PersistentReadRequest.ContentOneofCase.Ack:
                    await live.AcknowledgeAsync(ParseIds(requestStream.Current.Ack.Ids)).ConfigureAwait(false);
                    break;
                case PersistentReadRequest.ContentOneofCase.Nack:
                    var nack = requestStream.Current.Nack;
                    var action = nack.Action switch
                    {
                        Protocol.V1.NackAction.Park => Nightingale.NackAction.Park,
                        Protocol.V1.NackAction.Skip => Nightingale.NackAction.Skip,
                        _ => Nightingale.NackAction.Retry,
                    };
                    await live.RefuseAsync(ParseIds(nack.Ids), action, nack.Reason).ConfigureAwait(false);
                    break;
                default:
                    throw NightingaleErrors.InvalidArgument("After the options, every message of a persistent read acknowledges or refuses events.");
            }
        }
    }

    /// <summary>Renews the lease, redelivers what timed out and writes how the group stands, on a cadence tied to the message timeout.</summary>
    private async Task KeepAsync(SubscriptionGroupRuntime live, SubscriptionGroupDefinition definition, string lease, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromTicks(Math.Min(definition.Settings.MessageTimeout.Ticks / 2, LeaseDuration.Ticks / 3));
        while (true)
        {
            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
            var owner = await groups.AcquireLeaseAsync(lease, registry.InstanceId, address.Current, LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (owner is not null)
            {
                throw NightingaleErrors.GroupOwnedElsewhere(definition.Stream, definition.Group, owner);
            }

            await live.ExpireAsync().ConfigureAwait(false);
            await groups.SaveLiveAsync(definition.Id, live.Describe(), cancellationToken).ConfigureAwait(false);
        }
    }
}
