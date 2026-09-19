using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The <c>PersistentSubscriptions</c> service over the group store and the stream store. Create,
/// delete and replay go straight to the group store. A read takes the group's lease for this
/// instance, refuses a second consumer, runs the group in memory for as long as the consumer
/// stays, and turns the consumer's acknowledgements into the group's checkpoint.
/// </summary>
/// <param name="groups">The group store.</param>
/// <param name="store">The stream store.</param>
/// <param name="tail">The tail.</param>
/// <param name="registry">The groups live in this instance.</param>
/// <param name="timeProvider">The clock.</param>
public sealed class PersistentSubscriptionsService(IGroupStore groups, IStreamStore store, IStoreTail tail, GroupRegistry registry, TimeProvider timeProvider) : PersistentSubscriptions.PersistentSubscriptionsBase
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

        try
        {
            await groups.CreateAsync(new GroupDefinition(stream, group, settings, -1), context.CancellationToken).ConfigureAwait(false);
        }
        catch (GroupExistsException)
        {
            throw NightingaleErrors.GroupExists(stream, group);
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
    public override async Task<ReplayParkedResponse> ReplayParked(ReplayParkedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var stream = ReadableStreamName(request.Stream);
        var group = GroupName(request.Group);
        if (await groups.GetAsync(stream, group, context.CancellationToken).ConfigureAwait(false) is null)
        {
            throw NightingaleErrors.GroupNotFound(stream, group);
        }

        long? position = request.WhichCase == ReplayParkedRequest.WhichOneofCase.Position ? request.Position : null;
        var replayed = await groups.MarkForReplayAsync(stream, group, position, context.CancellationToken).ConfigureAwait(false);
        if (position is { } wanted && replayed == 0)
        {
            throw NightingaleErrors.ParkedMessageNotFound(stream, group, wanted);
        }

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

        if (!registry.TryClaim(stream, group))
        {
            throw NightingaleErrors.ConsumerLimitReached(stream, group, definition.Settings.MaxSubscriberCount);
        }

        try
        {
            var lease = GroupRegistry.LeaseName(stream, group);
            var owner = await groups.AcquireLeaseAsync(lease, registry.InstanceId, LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (owner is not null)
            {
                throw NightingaleErrors.GroupOwnedElsewhere(stream, group, owner);
            }

            try
            {
                await ServeAsync(definition, buffer, lease, requestStream, responseStream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await groups.ReleaseLeaseAsync(lease, registry.InstanceId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            registry.Release(stream, group);
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

    private async Task ServeAsync(GroupDefinition definition, int buffer, string lease, IAsyncStreamReader<PersistentReadRequest> requestStream, IServerStreamWriter<PersistentReadResponse> responseStream, CancellationToken cancellationToken)
    {
        await using var live = new PersistentGroup(store, tail, groups, timeProvider, definition, buffer);
        await responseStream.WriteAsync(
            new PersistentReadResponse { Confirmed = new PersistentSubscriptionConfirmed { SubscriptionId = Guid.NewGuid().ToString("D"), Checkpoint = live.Checkpoint } },
            cancellationToken).ConfigureAwait(false);

        using var ending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = ending.Token;
        var sending = SendAsync(live, responseStream, token);
        var receiving = ReceiveAsync(live, requestStream, token);
        var keeping = KeepAsync(live, lease, definition.Settings.MessageTimeout, token);

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

    private static async Task SendAsync(PersistentGroup live, IServerStreamWriter<PersistentReadResponse> responseStream, CancellationToken cancellationToken)
    {
        await foreach (var message in live.Outgoing.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(
                new PersistentReadResponse { Event = new PersistentEvent { Event = message.Record.ToRecordedEvent(), RetryCount = message.RetryCount } },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveAsync(PersistentGroup live, IAsyncStreamReader<PersistentReadRequest> requestStream, CancellationToken cancellationToken)
    {
        while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            switch (requestStream.Current.ContentCase)
            {
                case PersistentReadRequest.ContentOneofCase.Ack:
                    await live.AcknowledgeAsync(Ids(requestStream.Current.Ack.Ids), cancellationToken).ConfigureAwait(false);
                    break;
                case PersistentReadRequest.ContentOneofCase.Nack:
                    var nack = requestStream.Current.Nack;
                    var action = nack.Action switch
                    {
                        Protocol.V1.NackAction.Park => Nightingale.NackAction.Park,
                        Protocol.V1.NackAction.Skip => Nightingale.NackAction.Skip,
                        _ => Nightingale.NackAction.Retry,
                    };
                    await live.RefuseAsync(Ids(nack.Ids), action, nack.Reason, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw NightingaleErrors.InvalidArgument("After the options, every message of a persistent read acknowledges or refuses events.");
            }
        }
    }

    /// <summary>Renews the lease and redelivers what timed out, on a cadence tied to the message timeout.</summary>
    private async Task KeepAsync(PersistentGroup live, string lease, TimeSpan messageTimeout, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromTicks(Math.Min(messageTimeout.Ticks / 2, LeaseDuration.Ticks / 3));
        while (true)
        {
            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
            var owner = await groups.AcquireLeaseAsync(lease, registry.InstanceId, LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (owner is not null)
            {
                throw NightingaleErrors.GroupOwnedElsewhere(live.ToString() ?? string.Empty, lease, owner);
            }

            await live.ExpireAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
