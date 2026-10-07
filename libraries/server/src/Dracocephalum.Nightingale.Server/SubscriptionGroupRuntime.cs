using System.Text;
using System.Threading.Channels;

using Dracocephalum.Nightingale;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// A persistent-subscription group while consumers are connected to it, in memory in the
/// instance that owns its lease. It reads the group's stream from the checkpoint, delivers each
/// event to one consumer, the one the group's strategy picks among those with room, and keeps
/// every delivered event in flight until it is acknowledged, refused, or times out. The
/// checkpoint is the last position every delivered event up to which is done, acknowledged,
/// skipped or parked, and is written, whenever it has moved, on the group's policy and when the
/// group stops. A group over a stream counts in revisions; one over <c>$all</c> or a virtual
/// stream in positions, or in ordinals when it was created under ordinal numbering, which then
/// keys its checkpoint and its parked messages too. Delivery has the reference's shape: a list
/// of retries served before the live buffer. What is due on the outbox joins the retries, when
/// the group starts and whenever it is woken, which a replay does, so a replayed message goes
/// out ahead of the next event the stream would have sent; a refused event that may be tried
/// again goes straight back, ahead of anything new. What a consumer asks for, an acknowledgement,
/// a refusal, is applied to the store to completion even when the consumer leaves the moment it
/// asked: its departure ends its deliveries, never the bookkeeping, and what it had in flight
/// goes back to be delivered to the consumers that remain, not counted against the event.
/// </summary>
internal sealed class SubscriptionGroupRuntime : IAsyncDisposable
{
    private readonly IStreamStore _store;
    private readonly IStoreTail _tail;
    private readonly ISubscriptionGroupStore _groups;
    private readonly TimeProvider _time;
    private readonly SubscriptionGroupDefinition _definition;
    private readonly bool _byPosition;
    private readonly bool _byOrdinal;
    private readonly VirtualStreamName _virtual;
    private readonly long? _from;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, InFlight> _inFlight = [];
    private readonly SortedDictionary<long, bool> _delivered = [];
    private readonly Queue<InFlight> _retries = [];
    private readonly HashSet<Guid> _queued = [];
    private readonly List<SubscriptionConsumer> _consumers = [];
    private readonly Channel<EventRecord> _live = Channel.CreateBounded<EventRecord>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
    private readonly Channel<bool> _wakes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _draining = new(1, 1);
    private readonly DateTimeOffset _startedAt;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _reader;
    private readonly Task _dispatcher;
    private readonly Task _drainer;
    private TaskCompletionSource _retryArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _consumersChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InFlight? _heldLive;
    private int _roundRobin;
    private int _membership;
    private Exception? _failure;
    private long _checkpoint;
    private long _written;
    private int _doneSinceWrite;
    private DateTimeOffset _lastWrite;

    /// <summary>Initializes a new instance of the <see cref="SubscriptionGroupRuntime"/> class and starts reading; delivery begins when a consumer joins.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tail">The tail.</param>
    /// <param name="groups">The group store.</param>
    /// <param name="time">The clock.</param>
    /// <param name="definition">The group as read from the store.</param>
    public SubscriptionGroupRuntime(IStreamStore store, IStoreTail tail, ISubscriptionGroupStore groups, TimeProvider time, SubscriptionGroupDefinition definition)
    {
        _store = store;
        _tail = tail;
        _groups = groups;
        _time = time;
        _definition = definition;
        _checkpoint = definition.Checkpoint;
        _written = definition.Checkpoint;
        _lastWrite = time.GetUtcNow();
        _startedAt = _lastWrite;
        _byOrdinal = definition.Settings.Numbering == Numbering.Ordinal && StreamNames.TryParseVirtual(definition.Stream, out _virtual);
        _byPosition = StreamNames.IsReserved(definition.Stream) && !_byOrdinal;

        // Where the feed starts is settled now, not on the reader's thread: from the end of $all or
        // a virtual stream by position means after the head as it is at this moment. A plain stream
        // from its end needs the stream's own head, and a virtual stream by ordinal its last
        // ordinal, which are reads the reader does.
        _from = definition.Checkpoint >= 0 ? definition.Checkpoint + 1
            : !definition.Settings.Start.IsEnd ? definition.Settings.Start.Value
            : _byPosition ? tail.Head + 1
            : null;

        _reader = Task.Run(() => ReadAsync(_stopping.Token));
        _dispatcher = Task.Run(() => DispatchAsync(_stopping.Token));
        _drainer = Task.Run(() => DrainOnWakeAsync(_stopping.Token));
        Delivery = WatchAsync();
    }

    /// <summary>Gets the group's checkpoint as it stands.</summary>
    public long Checkpoint
    {
        get
        {
            lock (_gate)
            {
                return _checkpoint;
            }
        }
    }

    /// <summary>Gets a task that faults when delivery fails; every consumer's channel is then completed with the cause.</summary>
    public Task Delivery { get; }

    /// <summary>Gets the group as read from the store when it was started.</summary>
    public SubscriptionGroupDefinition Definition => _definition;

    /// <summary>Gets how many consumers are connected.</summary>
    public int ConsumerCount
    {
        get
        {
            lock (_gate)
            {
                return _consumers.Count;
            }
        }
    }

    /// <summary>
    /// Adds a consumer: from now on the strategy may pick it. Its events arrive on its channel, in
    /// delivery order, and the channel is completed, with the cause, when the group has no more
    /// for it: a group that stopped, failed or was updated.
    /// </summary>
    /// <param name="bufferSize">How many delivered, unacknowledged events the consumer holds at once.</param>
    /// <param name="address">Where the consumer connected from.</param>
    /// <returns>The consumer; give it back with <see cref="Leave"/>.</returns>
    public SubscriptionConsumer Join(int bufferSize, string? address)
    {
        var consumer = new SubscriptionConsumer(bufferSize, address, _time.GetUtcNow());
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (_failure is { } failure)
            {
                consumer.Outgoing.Writer.TryComplete(failure);
                return consumer;
            }

            _consumers.Add(consumer);
            _membership++;
            changed = _consumersChanged;
            _consumersChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
        return consumer;
    }

    /// <summary>
    /// Removes a consumer. What it had in flight goes back to be delivered to the consumers that
    /// remain, with its retry count as it was: the event did nothing wrong. What it had already
    /// asked for, an acknowledgement on its way, is still applied when it arrives.
    /// </summary>
    /// <param name="consumer">The consumer, as <see cref="Join"/> gave it.</param>
    public void Leave(SubscriptionConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        List<InFlight> orphaned;
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (!_consumers.Remove(consumer))
            {
                return;
            }

            orphaned = _inFlight.Values.Where(flight => flight.ConsumerId == consumer.Id).ToList();
            foreach (var flight in orphaned)
            {
                _inFlight.Remove(flight.Record.Id);
            }

            consumer.InFlightCount = 0;
            _membership++;
            changed = _consumersChanged;
            _consumersChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        consumer.Outgoing.Writer.TryComplete();
        changed.TrySetResult();
        foreach (var flight in orphaned)
        {
            EnqueueRetry(flight with { ConsumerId = Guid.Empty, Deadline = default });
        }
    }

    /// <summary>How the group stands right now: its consumers and what it has outstanding.</summary>
    /// <returns>The numbers, taken together under the group's lock.</returns>
    public SubscriptionGroupLive Describe()
    {
        lock (_gate)
        {
            // An event's deadline is when it was sent plus the message timeout, and one not sent
            // yet has none; the oldest delivery is the earliest deadline, less that timeout.
            DateTimeOffset? oldest = null;
            foreach (var flight in _inFlight.Values)
            {
                if (flight.Deadline != default && (oldest is null || flight.Deadline < oldest))
                {
                    oldest = flight.Deadline;
                }
            }

            var consumers = _consumers.Select(consumer => new SubscriptionConsumerLive(consumer.ConnectedAt, consumer.Address, consumer.BufferSize, consumer.InFlightCount)).ToList();
            return new SubscriptionGroupLive(
                consumers.Count > 0 ? consumers.Min(consumer => consumer.ConnectedAt) : _startedAt,
                _inFlight.Count,
                _retries.Count,
                consumers.Sum(consumer => consumer.BufferSize),
                _checkpoint >= 0 ? _checkpoint : null,
                oldest - _definition.Settings.MessageTimeout,
                consumers.Count > 0 ? consumers[0].Address : null,
                _time.GetUtcNow(),
                consumers.Count,
                consumers);
        }
    }

    /// <summary>Wakes the group: it looks at its outbox and queues what is due, ahead of the stream. Coalesces.</summary>
    public void Wake() => _wakes.Writer.TryWrite(true);

    /// <summary>
    /// Ends every consumer's deliveries with a cause, and refuses any that joins after: the
    /// group was updated, or the instance lost its lease. The bookkeeping goes on until the
    /// group is disposed, so an acknowledgement that was on its way is still applied.
    /// </summary>
    /// <param name="cause">What every consumer's call ends with.</param>
    public void Fail(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        List<SubscriptionConsumer> consumers;
        lock (_gate)
        {
            _failure ??= cause;
            consumers = [.. _consumers];
        }

        foreach (var consumer in consumers)
        {
            consumer.Outgoing.Writer.TryComplete(cause);
        }
    }

    /// <summary>A consumer is done with these events.</summary>
    /// <param name="ids">The event ids.</param>
    /// <returns>A task that completes when the checkpoint policy has been applied.</returns>
    public async Task AcknowledgeAsync(IEnumerable<Guid> ids)
    {
        var toDequeue = new List<long>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!_inFlight.Remove(id, out var flight))
                {
                    continue;
                }

                ReleaseRoom(flight);
                MarkDone(GetKey(flight.Record));
                if (flight.FromOutbox)
                {
                    toDequeue.Add(flight.Record.Position);
                }
            }
        }

        foreach (var position in toDequeue)
        {
            await _groups.DequeueAsync(_definition.Id, position, CancellationToken.None).ConfigureAwait(false);
        }

        await WriteCheckpointIfDueAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>A consumer could not process these events.</summary>
    /// <param name="ids">The event ids.</param>
    /// <param name="action">What to do with them.</param>
    /// <param name="reason">The consumer's reason, kept with a parked message.</param>
    /// <returns>A task that completes when the events are redelivered, parked or dropped.</returns>
    public async Task RefuseAsync(IEnumerable<Guid> ids, NackAction action, string reason)
    {
        var toPark = new List<InFlight>();
        var toDequeue = new List<long>();
        var toRedeliver = new List<InFlight>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!_inFlight.Remove(id, out var flight))
                {
                    continue;
                }

                ReleaseRoom(flight);
                switch (action)
                {
                    case NackAction.Skip:
                        MarkDone(GetKey(flight.Record));
                        if (flight.FromOutbox)
                        {
                            toDequeue.Add(flight.Record.Position);
                        }

                        break;
                    case NackAction.Park:
                        MarkDone(GetKey(flight.Record));
                        toPark.Add(flight);
                        break;
                    default:
                        Requeue(flight, toPark, toRedeliver);
                        break;
                }
            }
        }

        await SettleAsync(toRedeliver, toPark, reason).ConfigureAwait(false);
        foreach (var position in toDequeue)
        {
            await _groups.DequeueAsync(_definition.Id, position, CancellationToken.None).ConfigureAwait(false);
        }

        await WriteCheckpointIfDueAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Redelivers whatever has been in flight longer than the group's message timeout.</summary>
    /// <returns>A task that completes when the expired events are redelivered or parked.</returns>
    public async Task ExpireAsync()
    {
        var toPark = new List<InFlight>();
        var toRedeliver = new List<InFlight>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var flight in _inFlight.Values.Where(flight => flight.Deadline <= now).ToList())
            {
                _inFlight.Remove(flight.Record.Id);
                ReleaseRoom(flight);
                Requeue(flight, toPark, toRedeliver);
            }
        }

        await SettleAsync(toRedeliver, toPark, "Retry limit reached after the message timeout.").ConfigureAwait(false);
        await WriteCheckpointIfDueAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _wakes.Writer.TryComplete();
        _live.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_reader, _dispatcher, _drainer).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping is the point; a delivery failure was already surfaced through Delivery.
        }

        List<SubscriptionConsumer> consumers;
        lock (_gate)
        {
            consumers = [.. _consumers];
            _consumers.Clear();
        }

        foreach (var consumer in consumers)
        {
            consumer.Outgoing.Writer.TryComplete();
        }

        await WriteCheckpointIfDueAsync(true, CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
        _draining.Dispose();
    }

    /// <summary>What an event is pinned by under correlation pinning: its correlation id, or its stream name when it has none.</summary>
    private static string GetPinKey(EventRecord record) =>
        record.Metadata[MetadataKeys.CorrelationId] is { } node && node.GetValueKind() == System.Text.Json.JsonValueKind.String && node.GetValue<string>() is { Length: > 0 } correlation
            ? correlation
            : record.Stream;

    /// <summary>The number of a key's consumer under the pinned strategies: the same key, the same consumer, while the consumers are the same.</summary>
    private static int Hash(string key)
    {
        // FNV-1a over the key's bytes: the same in every process, which string hashing is not,
        // so two instances in turn pin a key the same way.
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return (int)(hash & 0x7FFFFFFF);
    }

    private long GetKey(EventRecord record) =>
        _byOrdinal ? record.Ordinal ?? throw new InvalidOperationException("The store returned an event without its ordinal from an ordinal read.")
        : _byPosition ? record.Position
        : record.Revision;

    private void MarkDone(long key)
    {
        _delivered[key] = true;
        _doneSinceWrite++;
        while (_delivered.Count > 0)
        {
            var first = _delivered.First();
            if (!first.Value)
            {
                break;
            }

            // A replayed message is done for the second time, below a checkpoint it once moved;
            // the checkpoint only ever advances.
            _delivered.Remove(first.Key);
            _checkpoint = Math.Max(_checkpoint, first.Key);
        }
    }

    /// <summary>Gives a delivered event's slot back to its consumer, and lets the dispatcher know there is room. Under the lock.</summary>
    private void ReleaseRoom(InFlight flight)
    {
        var consumer = _consumers.Find(candidate => candidate.Id == flight.ConsumerId);
        if (consumer is null)
        {
            return;
        }

        consumer.InFlightCount--;
        var changed = _consumersChanged;
        _consumersChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    private void Requeue(InFlight flight, List<InFlight> toPark, List<InFlight> toRedeliver)
    {
        if (flight.Attempts >= _definition.Settings.MaxRetryCount)
        {
            MarkDone(GetKey(flight.Record));
            toPark.Add(flight);
            return;
        }

        // Straight back, ahead of anything new, to whichever consumer the strategy picks then.
        toRedeliver.Add(flight with { Attempts = flight.Attempts + 1, ConsumerId = Guid.Empty, Deadline = default });
    }

    private async Task SettleAsync(List<InFlight> toRedeliver, List<InFlight> toPark, string reason)
    {
        foreach (var flight in toRedeliver)
        {
            EnqueueRetry(flight);
        }

        foreach (var flight in toPark)
        {
            // Parking moves a message off the outbox in the same write, so one that came from
            // there and failed again is back where it was, with its new reason and count.
            await _groups.ParkAsync(
                new SubscriptionParkedMessage(_definition.Id, flight.Record.Position, flight.Record.Revision, flight.Record.Ordinal, flight.Record.Id, reason, flight.Attempts, _time.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task WriteCheckpointIfDueAsync(bool force, CancellationToken cancellationToken)
    {
        long checkpoint;
        lock (_gate)
        {
            var settings = _definition.Settings;
            var elapsed = _time.GetUtcNow() - _lastWrite;
            var due = force
                || _doneSinceWrite >= settings.CheckpointUpperBound
                || (elapsed >= settings.CheckpointAfter && _doneSinceWrite >= settings.CheckpointLowerBound);
            if (!due || _checkpoint <= _written)
            {
                return;
            }

            checkpoint = _checkpoint;
            _written = checkpoint;
            _doneSinceWrite = 0;
            _lastWrite = _time.GetUtcNow();
        }

        await _groups.SaveCheckpointAsync(_definition.Id, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends every consumer's deliveries when the reader or the dispatcher fails, with the cause.</summary>
    private async Task WatchAsync()
    {
        try
        {
            await Task.WhenAny(_reader, _dispatcher).Unwrap().ConfigureAwait(false);
        }
        catch (Exception cause) when (!_stopping.IsCancellationRequested)
        {
            Fail(cause);
            throw;
        }
    }

    /// <summary>Reads the stream from where the group stands into the live buffer, after the outbox has been queued.</summary>
    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DrainOutboxAsync(cancellationToken).ConfigureAwait(false);
            var from = _from ?? await HeadAsync(cancellationToken).ConfigureAwait(false) + 1;
            await foreach (var record in EventSource.FollowAsync(_store, _tail, _definition.Stream, from, _definition.Settings.BufferSize, _definition.Settings.Numbering, _time, cancellationToken).ConfigureAwait(false))
            {
                await _live.Writer.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _live.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Hands each event to the consumer the strategy picks, retries first, then the live buffer:
    /// the reference's order. No event is taken until a consumer has room for it, so a retry
    /// queued while every consumer is full still goes ahead of the next event of the stream;
    /// and an event taken for a consumer that then leaves, or that is full when another joins,
    /// goes back where it came from, so what a leaving consumer held goes out first.
    /// </summary>
    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitForRoomAsync(cancellationToken).ConfigureAwait(false);
            var (next, retry) = await NextAsync(cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                return;
            }

            var consumer = await ChooseAsync(next.Record, cancellationToken).ConfigureAwait(false);
            if (consumer is null)
            {
                Unget(next, retry);
                continue;
            }

            await SendAsync(next, consumer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until a consumer has room for one more event: the single one, under that strategy, else any.</summary>
    private async Task WaitForRoomAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var room = _definition.Settings.ConsumerStrategy == ConsumerStrategy.DispatchToSingle
                    ? _consumers.Count > 0 && _consumers[0].HasRoom
                    : _consumers.Exists(consumer => consumer.HasRoom);
                if (room)
                {
                    return;
                }

                changed = _consumersChanged.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Puts an event that could not be sent back where it was taken from: a retry at the head of the retries, a live event ahead of the live buffer.</summary>
    private void Unget(InFlight flight, bool retry)
    {
        lock (_gate)
        {
            if (retry)
            {
                var retries = _retries.ToList();
                _retries.Clear();
                _retries.Enqueue(flight);
                foreach (var queued in retries)
                {
                    _retries.Enqueue(queued);
                }

                _queued.Add(flight.Record.Id);
            }
            else
            {
                _heldLive = flight;
            }
        }
    }

    /// <summary>The next event to send, and whether it is a retry: a queued retry if there is one, else the next live event, whichever comes first.</summary>
    private async Task<(InFlight? Flight, bool Retry)> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task arrived;
            lock (_gate)
            {
                if (_retries.TryDequeue(out var retry))
                {
                    _queued.Remove(retry.Record.Id);
                    return (retry, true);
                }

                if (_heldLive is { } held)
                {
                    _heldLive = null;
                    return (held, false);
                }

                arrived = _retryArrived.Task;
            }

            var live = _live.Reader.WaitToReadAsync(cancellationToken).AsTask();
            if (await Task.WhenAny(live, arrived).ConfigureAwait(false) == arrived)
            {
                continue;
            }

            if (!await live.ConfigureAwait(false))
            {
                return (null, false);
            }

            if (_live.Reader.TryRead(out var record))
            {
                return (new InFlight(record, 0, default, false, Guid.Empty), false);
            }
        }
    }

    /// <summary>
    /// The consumer an event goes to, by the group's strategy, once it has room. In turn that is
    /// one with room; a pinned stream's consumer may be full, and then the dispatcher holds the
    /// event until it has room, since going to another consumer is what would break the order
    /// the strategy is there to keep. When the consumers change meanwhile, the answer is none:
    /// the choice is to be made again, after what a leaving consumer held.
    /// </summary>
    private async Task<SubscriptionConsumer?> ChooseAsync(EventRecord record, CancellationToken cancellationToken)
    {
        int membership;
        lock (_gate)
        {
            membership = _membership;
        }

        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_membership != membership)
                {
                    return null;
                }

                if (_consumers.Count > 0)
                {
                    var chosen = _definition.Settings.ConsumerStrategy switch
                    {
                        ConsumerStrategy.DispatchToSingle => _consumers[0].HasRoom ? _consumers[0] : null,
                        ConsumerStrategy.Pinned => _consumers[Hash(record.Stream) % _consumers.Count] is { HasRoom: true } pinned ? pinned : null,
                        ConsumerStrategy.PinnedByCorrelation => _consumers[Hash(GetPinKey(record)) % _consumers.Count] is { HasRoom: true } pinned ? pinned : null,
                        _ => NextWithRoom(),
                    };
                    if (chosen is not null)
                    {
                        chosen.InFlightCount++;
                        return chosen;
                    }
                }

                changed = _consumersChanged.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Round robin: the next consumer in turn that has room, or none. Under the lock.</summary>
    private SubscriptionConsumer? NextWithRoom()
    {
        for (var looked = 0; looked < _consumers.Count; looked++)
        {
            var candidate = _consumers[_roundRobin % _consumers.Count];
            _roundRobin = (_roundRobin + 1) % _consumers.Count;
            if (candidate.HasRoom)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Queues a retry and lets the dispatcher know.</summary>
    private void EnqueueRetry(InFlight flight)
    {
        TaskCompletionSource arrived;
        lock (_gate)
        {
            _retries.Enqueue(flight);
            _queued.Add(flight.Record.Id);
            arrived = _retryArrived;
            _retryArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        arrived.TrySetResult();
    }

    /// <summary>Looks at the outbox on every wake, so a replay reaches a connected consumer at once.</summary>
    private async Task DrainOnWakeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _wakes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _wakes.Reader.TryRead(out _);
                await DrainOutboxAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>
    /// Queues what is due on the outbox as retries, one drain at a time. A message already in
    /// flight or already queued is left alone: it is on the outbox until it is done, and a wake in
    /// the meantime must not deliver it twice. A message whose event is gone leaves the outbox.
    /// </summary>
    private async Task DrainOutboxAsync(CancellationToken cancellationToken)
    {
        await _draining.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var due = await _groups.DueAsync(_definition.Id, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            foreach (var message in due)
            {
                lock (_gate)
                {
                    if (_inFlight.ContainsKey(message.EventId) || _queued.Contains(message.EventId))
                    {
                        continue;
                    }
                }

                var record = await ReadOneAsync(message, cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    await _groups.DequeueAsync(_definition.Id, message.Position, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                EnqueueRetry(new InFlight(record, message.Attempts, default, true, Guid.Empty));
            }
        }
        finally
        {
            _draining.Release();
        }
    }

    private async Task SendAsync(InFlight flight, SubscriptionConsumer consumer, CancellationToken cancellationToken)
    {
        var timed = flight with { Deadline = _time.GetUtcNow() + _definition.Settings.MessageTimeout, ConsumerId = consumer.Id };
        lock (_gate)
        {
            _inFlight[timed.Record.Id] = timed;
            _delivered.TryAdd(GetKey(timed.Record), false);
        }

        // A consumer that left between being chosen and being written to has its channel
        // completed; the event is then among those it left in flight, and goes back out.
        try
        {
            await consumer.Outgoing.Writer.WriteAsync(new PersistentSubscriptionMessage.Recorded(timed.Record, timed.Attempts), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            Leave(consumer);
        }
    }

    /// <summary>The head a group from the end starts after: a plain stream's last revision, or a virtual stream's last ordinal.</summary>
    private async Task<long> HeadAsync(CancellationToken cancellationToken)
    {
        if (_byOrdinal)
        {
            var bounds = await _store.OrdinalHeadAsync(_virtual, cancellationToken).ConfigureAwait(false);
            return bounds?.Last ?? -1;
        }

        var page = await _store.ReadAsync(_definition.Stream, Direction.Forwards, long.MaxValue, 1, cancellationToken).ConfigureAwait(false);
        return page?.Head.Last ?? -1;
    }

    private async Task<EventRecord?> ReadOneAsync(SubscriptionOutboxMessage message, CancellationToken cancellationToken)
    {
        if (_byOrdinal)
        {
            if (message.Ordinal is not { } ordinal)
            {
                return null;
            }

            var page = await _store.ReadByOrdinalAsync(_virtual, Direction.Forwards, ordinal, 1, cancellationToken).ConfigureAwait(false);
            return page.Count == 1 && page[0].Ordinal == ordinal ? page[0] : null;
        }

        if (_definition.Stream == StreamNames.All)
        {
            var page = await _store.ReadAllAsync(Direction.Forwards, message.Position, message.Position, 1, cancellationToken).ConfigureAwait(false);
            return page.Count == 1 ? page[0] : null;
        }

        if (StreamNames.TryParseVirtual(_definition.Stream, out var virtualStream))
        {
            var page = await _store.ReadVirtualAsync(virtualStream, Direction.Forwards, message.Position, message.Position, 1, cancellationToken).ConfigureAwait(false);
            return page.Count == 1 ? page[0] : null;
        }

        var slice = await _store.ReadAsync(_definition.Stream, Direction.Forwards, message.Revision, 1, cancellationToken).ConfigureAwait(false);
        return slice is { Events.Count: 1 } && slice.Events[0].Revision == message.Revision ? slice.Events[0] : null;
    }

    /// <summary>A delivered event a consumer has not answered for yet, and which consumer.</summary>
    private sealed record InFlight(EventRecord Record, int Attempts, DateTimeOffset Deadline, bool FromOutbox, Guid ConsumerId);
}
