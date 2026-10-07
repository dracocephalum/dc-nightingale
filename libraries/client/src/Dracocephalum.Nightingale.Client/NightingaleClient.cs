using System.Collections.Concurrent;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// The client of a Nightingale server, shaped after the reference event-store client: append to a
/// stream under an expected state, read a stream, $all or a virtual stream in either direction,
/// subscribe to any of them, and delete or tombstone a stream where the server allows it. A failed call surfaces as a
/// domain exception when the server gave a reason, an argument exception when the request was at
/// fault, and the raw call exception otherwise. A persistent-subscription group runs in one server
/// instance at a time; when another instance answers that the group runs elsewhere and says
/// where, the client goes there itself, once, the way the reference client follows a not-leader
/// answer, and keeps the connection for the next time. Thread-safe; one instance per server address.
/// </summary>
public sealed class NightingaleClient : IAsyncDisposable
{
    private readonly ConcurrentBag<GrpcChannel> _ownedChannels = [];
    private readonly ConcurrentDictionary<Uri, PersistentSubscriptions.PersistentSubscriptionsClient> _owners = new();
    private readonly Func<Uri, CallInvoker> _redirects;
    private readonly NightingaleClientSettings? _settings;
    private readonly Streams.StreamsClient _streams;
    private readonly PersistentSubscriptions.PersistentSubscriptionsClient _persistent;

    /// <summary>Initializes a new instance of the <see cref="NightingaleClient"/> class from a connection string; the client owns its channels.</summary>
    /// <param name="connectionString">The connection string, as <see cref="NightingaleClientSettings.Parse"/> reads it.</param>
    /// <exception cref="FormatException">The string is not a Nightingale connection string.</exception>
    public NightingaleClient(string connectionString)
        : this(NightingaleClientSettings.Parse(connectionString))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NightingaleClient"/> class that owns its channels.</summary>
    /// <param name="settings">How the server is reached.</param>
    public NightingaleClient(NightingaleClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        var channel = ClientChannels.Open(settings);
        _ownedChannels.Add(channel);
        var invoker = CreateInvoker(channel);
        _streams = new Streams.StreamsClient(invoker);
        _persistent = new PersistentSubscriptions.PersistentSubscriptionsClient(invoker);
        _redirects = OpenOwnedChannel;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NightingaleClient"/> class over a channel or
    /// invoker the caller owns and disposes, such as an in-process test server's.
    /// </summary>
    /// <param name="invoker">The call invoker.</param>
    /// <param name="redirects">Opens a connection to another instance when a group runs there; a channel of this client's own by default.</param>
    public NightingaleClient(CallInvoker invoker, Func<Uri, CallInvoker>? redirects = null)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _streams = new Streams.StreamsClient(invoker);
        _persistent = new PersistentSubscriptions.PersistentSubscriptionsClient(invoker);
        _redirects = redirects ?? OpenOwnedChannel;
    }

    /// <summary>Appends events to a stream atomically under an expected state.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream.</param>
    /// <param name="events">The events, in order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The revision and position of the last event written; the next append expects that revision.</returns>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    /// <exception cref="StreamDeletedException">The stream was deleted.</exception>
    /// <exception cref="ArgumentException">The server rejected the request as invalid.</exception>
    public async Task<AppendResult> AppendToStreamAsync(string stream, StreamState expected, IEnumerable<EventData> events, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentNullException.ThrowIfNull(events);

        try
        {
            using var call = _streams.Append(cancellationToken: cancellationToken);
            await call.RequestStream.WriteAsync(
                new AppendRequest { Options = new AppendOptions { Stream = stream, ExpectedRevision = expected.ToInt64() } },
                cancellationToken).ConfigureAwait(false);
            foreach (var eventData in events)
            {
                await call.RequestStream.WriteAsync(new AppendRequest { Event = eventData.ToProposedEvent() }, cancellationToken).ConfigureAwait(false);
            }

            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            var response = await call.ResponseAsync.ConfigureAwait(false);
            return new AppendResult(response.Revision, response.Position);
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    /// <summary>
    /// Reads a stream. The call starts immediately; the result reports the stream's state and head
    /// before any event and streams the events after. A virtual stream can be read by ordinal, in
    /// which case <paramref name="from"/>, the head and each event's ordinal are its dense numbers;
    /// the server refuses that on a store initialized without ordinals, and on any other stream.
    /// </summary>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="stream">The stream name.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="maxCount">The most events to return.</param>
    /// <param name="numbering">How the numbers of the read are meant; global by default.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The read in progress.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is not positive.</exception>
    public ReadStreamResult ReadStreamAsync(Direction direction, string stream, StreamPosition from, long maxCount = long.MaxValue, Numbering numbering = Numbering.Global, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        var request = new ReadRequest
        {
            Stream = stream,
            Direction = direction == Direction.Backwards ? ReadDirection.Backwards : ReadDirection.Forwards,
            Count = (ulong)maxCount,
            Numbering = numbering.ToWire(),
        };
        SetFrom(request, from);
        return new ReadStreamResult(stream, _streams.Read(request, cancellationToken: cancellationToken), cancellationToken);
    }

    private static async Task CallUnaryAsync(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    private static void SetFrom(ReadRequest request, StreamPosition from)
    {
        if (from.IsEnd)
        {
            request.End = new Google.Protobuf.WellKnownTypes.Empty();
        }
        else if (from == StreamPosition.Start)
        {
            request.Start = new Google.Protobuf.WellKnownTypes.Empty();
        }
        else
        {
            request.Position = from.Value;
        }
    }

    private StreamSubscription Subscribe(string stream, StreamPosition from, Numbering numbering, CancellationToken cancellationToken)
    {
        var request = new ReadRequest { Stream = stream, Direction = ReadDirection.Forwards, Subscription = new SubscriptionOptions(), Numbering = numbering.ToWire() };
        SetFrom(request, from);
        return new StreamSubscription(stream, _streams.Read(request, cancellationToken: cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Reads <c>$all</c>, every event in position order. The head the result reports is the
    /// high-water mark, 0 when there are no events; the result never reports a missing stream.
    /// </summary>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="maxCount">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The read in progress.</returns>
    public ReadStreamResult ReadAllAsync(Direction direction, StreamPosition from, long maxCount = long.MaxValue, CancellationToken cancellationToken = default) =>
        ReadStreamAsync(direction, StreamNames.All, from, maxCount, Numbering.Global, cancellationToken);

    /// <summary>
    /// Subscribes to a stream: every event from <paramref name="from"/> to the head, a caught-up
    /// note, then every event as it is appended, until the subscription is disposed. A stream
    /// that does not exist yet is waited for. A virtual stream can be followed by ordinal, in which
    /// case every number in the conversation is its dense one; the server refuses that on a store
    /// initialized without ordinals, and on any other stream.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="from">Where to begin, inclusive; <see cref="StreamPosition.End"/> for only what comes after.</param>
    /// <param name="numbering">How the numbers of the subscription are meant; global by default.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The subscription in progress.</returns>
    public StreamSubscription SubscribeToStreamAsync(string stream, StreamPosition from, Numbering numbering = Numbering.Global, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        return Subscribe(stream, from, numbering, cancellationToken);
    }

    /// <summary>Subscribes to <c>$all</c>; see <see cref="SubscribeToStreamAsync"/>.</summary>
    /// <param name="from">Where to begin, inclusive; <see cref="StreamPosition.End"/> for only what comes after.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The subscription in progress.</returns>
    public StreamSubscription SubscribeToAllAsync(StreamPosition from, CancellationToken cancellationToken = default) =>
        Subscribe(StreamNames.All, from, Numbering.Global, cancellationToken);

    /// <summary>
    /// Deletes a stream: its events leave every read and it cannot be appended to again. Only when
    /// the server's host allows deletion; it does not by default.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the stream is deleted.</returns>
    /// <exception cref="DeletionDisabledException">The server does not allow deletion.</exception>
    /// <exception cref="StreamNotFoundException">The stream has no events.</exception>
    /// <exception cref="StreamDeletedException">The stream was already deleted.</exception>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    public Task DeleteStreamAsync(string stream, StreamState expected, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        return CallUnaryAsync(async () => await _streams.DeleteAsync(new DeleteRequest { Stream = stream, ExpectedRevision = expected.ToInt64() }, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Tombstones a stream: it and its events are removed for good and the name reads as one that
    /// never existed. Only when the server's host allows it; it does not by default.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the stream is gone.</returns>
    /// <exception cref="DeletionDisabledException">The server does not allow tombstoning.</exception>
    /// <exception cref="StreamNotFoundException">The stream has no events.</exception>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    public Task TombstoneStreamAsync(string stream, StreamState expected, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        return CallUnaryAsync(async () => await _streams.TombstoneAsync(new TombstoneRequest { Stream = stream, ExpectedRevision = expected.ToInt64() }, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Creates a persistent-subscription group over a stream, <c>$all</c> or a virtual stream.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name, unique per stream.</param>
    /// <param name="settings">The settings; the defaults when null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the group exists.</returns>
    /// <exception cref="GroupExistsException">A group with that name exists on the stream.</exception>
    public Task CreatePersistentSubscriptionAsync(string stream, string group, GroupSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        return CallUnaryAsync(async () => await _persistent.CreateAsync(new CreateRequest { Stream = stream, Group = group, Settings = (settings ?? GroupSettings.Default).ToWire() }, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Deletes a persistent-subscription group, its checkpoint and its parked messages.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the group is gone.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    public Task DeletePersistentSubscriptionAsync(string stream, string group, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        return CallUnaryAsync(async () => await _persistent.DeleteAsync(new DeleteGroupRequest { Stream = stream, Group = group }, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Puts parked messages back in front of a group: all of them, or the one at a position.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="position">The parked message's position, or null for all.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many messages were put back.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    /// <exception cref="ParkedMessageNotFoundException">No parked message at that position.</exception>
    public async Task<int> ReplayParkedMessagesAsync(string stream, string group, long? position = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        var request = new ReplayParkedRequest { Stream = stream, Group = group };
        if (position is { } wanted)
        {
            request.Position = wanted;
        }
        else
        {
            request.All = new Google.Protobuf.WellKnownTypes.Empty();
        }

        try
        {
            return await ReplayAsync(_persistent, request, cancellationToken).ConfigureAwait(false);
        }
        catch (GroupOwnedElsewhereException elsewhere) when (elsewhere.Address is { } address)
        {
            return await ReplayAsync(GetOwnerClient(address), request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Changes a persistent-subscription group's settings in place: its timeouts, its retry
    /// limit, its checkpoint bounds, its buffer size and its consumer limit, all taken from the
    /// settings given. Where the group starts and how it is numbered are fixed when it is
    /// created, so those two members are not sent and stay as they are. A consumer connected to
    /// the group is disconnected with <see cref="GroupUpdatedException"/> and connects again
    /// under the new settings. When the group runs in another instance that says where, the
    /// client makes the change there instead, once.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="settings">The settings to change to; its start and its numbering are ignored.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The group's settings after the change, with the start and the numbering it has.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    public async Task<GroupSettings> UpdatePersistentSubscriptionAsync(string stream, string group, GroupSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        ArgumentNullException.ThrowIfNull(settings);
        var wire = settings.ToWire();
        wire.ClearStart();
        wire.Numbering = Protocol.V1.Numbering.Unspecified;
        var request = new UpdateRequest { Stream = stream, Group = group, Settings = wire };
        try
        {
            return await UpdateAsync(_persistent, request, cancellationToken).ConfigureAwait(false);
        }
        catch (GroupOwnedElsewhereException elsewhere) when (elsewhere.Address is { } address)
        {
            return await UpdateAsync(GetOwnerClient(address), request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Moves every parked message below a number to the group's outbox, to be delivered again
    /// ahead of the stream, as <see cref="ReplayParkedMessagesAsync"/> moves all or one.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="before">The number the replayed messages are below, in the group's numbering.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many messages were put back.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    public async Task<int> ReplayParkedMessagesBeforeAsync(string stream, string group, long before, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        var request = new ReplayParkedRequest { Stream = stream, Group = group, Before = before };
        try
        {
            return await ReplayAsync(_persistent, request, cancellationToken).ConfigureAwait(false);
        }
        catch (GroupOwnedElsewhereException elsewhere) when (elsewhere.Address is { } address)
        {
            return await ReplayAsync(GetOwnerClient(address), request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes parked messages for good, so the group never delivers them: all of them, or the
    /// one at a number. The events stay in their streams.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="position">The number of the one message to remove, in the group's numbering; all of them when null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many messages were removed.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    /// <exception cref="ParkedMessageNotFoundException">No parked message at that position.</exception>
    public Task<int> SkipParkedMessagesAsync(string stream, string group, long? position = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        var request = new SkipParkedRequest { Stream = stream, Group = group };
        if (position is { } wanted)
        {
            request.Position = wanted;
        }
        else
        {
            request.All = new Google.Protobuf.WellKnownTypes.Empty();
        }

        return SkipAsync(request, cancellationToken);
    }

    /// <summary>Removes every parked message below a number for good.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="before">The number the removed messages are below, in the group's numbering.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many messages were removed.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    public Task<int> SkipParkedMessagesBeforeAsync(string stream, string group, long before, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        return SkipAsync(new SkipParkedRequest { Stream = stream, Group = group, Before = before }, cancellationToken);
    }

    /// <summary>
    /// Lists a group's parked messages in the order of their numbers, a page at a time: which
    /// event each is, why it was parked and at which retry count.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="after">Only messages above this number, the last of the page before; from the first when null.</param>
    /// <param name="limit">The most messages to return, 1000 at most.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page; fewer than the limit means there are no more.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    public async Task<IReadOnlyList<ParkedMessageInfo>> ListParkedMessagesAsync(string stream, string group, long? after = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _persistent.ListParkedAsync(BuildPageRequest(stream, group, after, limit), cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Messages.Select(message => message.ToParkedMessageInfo()).ToList();
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    /// <summary>
    /// Lists the messages on a group's outbox the same way: parked messages a replay put back,
    /// not delivered yet.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="after">Only messages above this number, the last of the page before; from the first when null.</param>
    /// <param name="limit">The most messages to return, 1000 at most.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page; fewer than the limit means there are no more.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    public async Task<IReadOnlyList<OutboxMessageInfo>> ListOutboxMessagesAsync(string stream, string group, long? after = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _persistent.ListOutboxAsync(BuildPageRequest(stream, group, after, limit), cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Messages.Select(message => message.ToOutboxMessageInfo()).ToList();
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    /// <summary>
    /// Describes a persistent-subscription group: its settings, its checkpoint, what is parked
    /// and on its outbox, the last number of its stream, and where it runs. While a consumer is
    /// connected, the instance that runs the group answers and adds what only it knows; when
    /// that is another instance that says where, the client asks there instead, once.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The group's description.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    public async Task<PersistentSubscriptionInfo> GetPersistentSubscriptionInfoAsync(string stream, string group, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        var request = new GetInfoRequest { Stream = stream, Group = group };
        try
        {
            return await InfoAsync(_persistent, request, cancellationToken).ConfigureAwait(false);
        }
        catch (GroupOwnedElsewhereException elsewhere) when (elsewhere.Address is { } address)
        {
            return await InfoAsync(GetOwnerClient(address), request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lists the persistent-subscription groups, every one or those of one stream, each with
    /// what the store holds about it. A running group's numbers are in the listing as the group
    /// last wrote them, a few seconds old; <see cref="GetPersistentSubscriptionInfoAsync"/> gives
    /// them as they are now, for one group.
    /// </summary>
    /// <param name="stream">The stream whose groups are listed, or <see langword="null"/> for every group.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The groups, ordered by stream and then by group.</returns>
    public async Task<IReadOnlyList<PersistentSubscriptionInfo>> ListPersistentSubscriptionsAsync(string? stream = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _persistent.ListAsync(new ListRequest { Stream = stream ?? string.Empty }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Groups.Select(info => info.ToPersistentSubscriptionInfo()).ToList();
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    /// <summary>
    /// Connects to a persistent-subscription group as its consumer and waits for the server's
    /// confirmation; the subscription then streams the events, each to be acknowledged or refused.
    /// When the group runs in another instance that says where, the client connects there
    /// instead, once.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="bufferSize">How many delivered, unacknowledged events to hold at once.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmed subscription.</returns>
    /// <exception cref="GroupNotFoundException">No such group.</exception>
    /// <exception cref="ConsumerLimitReachedException">The group already has its consumer.</exception>
    /// <exception cref="GroupOwnedElsewhereException">The group runs in another instance that advertises no address, or the instance it named refused too.</exception>
    public async Task<PersistentSubscription> SubscribeToPersistentSubscriptionAsync(string stream, string group, int bufferSize = 10, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        try
        {
            return await OpenAsync(_persistent, stream, group, bufferSize, cancellationToken).ConfigureAwait(false);
        }
        catch (GroupOwnedElsewhereException elsewhere) when (elsewhere.Address is { } address)
        {
            return await OpenAsync(GetOwnerClient(address), stream, group, bufferSize, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        foreach (var channel in _ownedChannels)
        {
            channel.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static async Task<PersistentSubscription> OpenAsync(PersistentSubscriptions.PersistentSubscriptionsClient client, string stream, string group, int bufferSize, CancellationToken cancellationToken)
    {
        var call = client.Read(cancellationToken: cancellationToken);
        var subscription = new PersistentSubscription(stream, group, call, cancellationToken);
        _ = subscription.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = stream, Group = group, BufferSize = bufferSize } });
        try
        {
            await subscription.Confirmed.ConfigureAwait(false);
            return subscription;
        }
        catch (Exception)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<GroupSettings> UpdateAsync(PersistentSubscriptions.PersistentSubscriptionsClient client, UpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.UpdateAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Settings.ToGroupSettings();
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    private static ListMessagesRequest BuildPageRequest(string stream, string group, long? after, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentException.ThrowIfNullOrEmpty(group);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var request = new ListMessagesRequest { Stream = stream, Group = group, Limit = limit };
        if (after is { } number)
        {
            request.After = number;
        }

        return request;
    }

    private async Task<int> SkipAsync(SkipParkedRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _persistent.SkipParkedAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Skipped;
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    private static async Task<PersistentSubscriptionInfo> InfoAsync(PersistentSubscriptions.PersistentSubscriptionsClient client, GetInfoRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetInfoAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Info.ToPersistentSubscriptionInfo();
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    private static async Task<int> ReplayAsync(PersistentSubscriptions.PersistentSubscriptionsClient client, ReplayParkedRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.ReplayParkedAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Replayed;
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    /// <summary>The persistent-subscriptions client for the instance at an address, opened once and kept.</summary>
    private PersistentSubscriptions.PersistentSubscriptionsClient GetOwnerClient(Uri address) =>
        _owners.GetOrAdd(address, target => new PersistentSubscriptions.PersistentSubscriptionsClient(_redirects(target)));

    private CallInvoker OpenOwnedChannel(Uri address)
    {
        var channel = _settings is null ? GrpcChannel.ForAddress(address) : ClientChannels.OpenTo(_settings, address);
        _ownedChannels.Add(channel);
        return CreateInvoker(channel);
    }

    private CallInvoker CreateInvoker(GrpcChannel channel) =>
        _settings?.DefaultDeadline is { } deadline
            ? channel.Intercept(new DefaultDeadlineInterceptor(deadline, TimeProvider.System))
            : channel.CreateCallInvoker();
}
