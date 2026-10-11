namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Hands the events of plain streams to the subscriptions that follow them, one read of the store
/// per advance of the tail for all of them. Without it each subscription re-reads its own stream
/// at every advance, almost always for nothing, so an advance costs one query per subscription;
/// with it an advance costs one read of where the new events are, their stream and revision
/// without their bodies, and one read per followed stream that moved, so the cost follows the
/// events, not the subscriptions. One router per tenant's store: a stream name is per tenant, and
/// under a sequence per tenant so is the tail. The router starts at the head as it stands and
/// routes every range the tail advances through; a subscription follows before it reads its stream
/// for the catch-up, so an event the catch-up does not see is one the router brings, and delivers
/// only in revision order, re-reading its stream when what the router brings is ahead of what it
/// has delivered, which an append whose number landed above a gap can do.
/// </summary>
/// <remarks>
/// A follower that leaves <see cref="FollowerBuffer"/> events unread lapses: the router does not
/// wait for it, so that a slow subscriber never holds up the others, and the subscriber reads its
/// stream itself from where it stands, the way the reference server drops a live subscription to
/// catching up when its buffer overflows. A range the router cannot read fails every follower
/// present with the cause, as reading its own stream would have failed each of them, and the
/// router goes on to the next advance; a tail that has stopped following the head ends the router,
/// which then refuses to be followed with the same cause.
/// </remarks>
public sealed class StreamRouter : IAsyncDisposable
{
    /// <summary>How many pointers one read of an advanced range asks for.</summary>
    public const int PointerPage = 512;

    /// <summary>How many events a follower may leave unread before it lapses.</summary>
    public const int FollowerBuffer = 512;

    private readonly IStreamStore _store;
    private readonly IStoreTail _tail;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<StreamFollowing>> _followers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _routing;
    private long _routed;
    private Exception? _failure;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="StreamRouter"/> class and starts routing from the head as it stands.</summary>
    /// <param name="store">The store of the tenant whose streams are routed.</param>
    /// <param name="tail">The tail of the sequence the store's positions are numbered in.</param>
    public StreamRouter(IStreamStore store, IStoreTail tail)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tail);
        _store = store;
        _tail = tail;
        _routed = tail.Head;
        _routing = Task.Run(() => RouteAsync(_stopping.Token));
    }

    /// <summary>Gets the position up to which every follower has been handed its stream's events.</summary>
    public long Routed => Volatile.Read(ref _routed);

    /// <summary>Gets how many followers there are.</summary>
    public int FollowerCount
    {
        get
        {
            lock (_gate)
            {
                return _followers.Values.Sum(followers => followers.Count);
            }
        }
    }

    /// <summary>Follows a stream from now on: every event of it at a position beyond <see cref="Routed"/> is handed over, until the follower lapses or leaves.</summary>
    /// <param name="stream">The stream name.</param>
    /// <returns>The follower's feed.</returns>
    /// <exception cref="InvalidOperationException">The router can route no more; the cause is the inner exception.</exception>
    public StreamFollowing Follow(string stream)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failure is not null)
            {
                throw _failure;
            }

            var following = new StreamFollowing(this, stream);
            if (!_followers.TryGetValue(stream, out var followers))
            {
                followers = [];
                _followers[stream] = followers;
            }

            followers.Add(following);
            return following;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _routing.ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>Takes a follower out; its feed is the follower's own to end.</summary>
    internal void Leave(StreamFollowing following)
    {
        lock (_gate)
        {
            if (_followers.TryGetValue(following.Stream, out var followers) && followers.Remove(following) && followers.Count == 0)
            {
                _followers.Remove(following.Stream);
            }
        }
    }

    private async Task RouteAsync(CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                var head = await _tail.WaitForAdvanceAsync(Routed, stopping).ConfigureAwait(false);
                try
                {
                    await RouteRangeAsync(Routed + 1, head, stopping).ConfigureAwait(false);
                }
                catch (Exception failure) when (!stopping.IsCancellationRequested)
                {
                    FailEveryone(failure);
                }

                Volatile.Write(ref _routed, head);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Disposed.
        }
        catch (Exception failure)
        {
            // The tail has stopped following the head: nothing more will ever be routed, and
            // whoever follows, now or later, is told so rather than left waiting.
            lock (_gate)
            {
                _failure = failure;
            }

            FailEveryone(failure);
        }
    }

    /// <summary>Routes the events at positions from one up to another, both inclusive: the pointers of the range, page by page, then the events of each followed stream that moved.</summary>
    private async Task RouteRangeAsync(long from, long head, CancellationToken cancellationToken)
    {
        while (from <= head && HasFollowers())
        {
            var pointers = await _store.ReadPointersAsync(from, head, PointerPage, cancellationToken).ConfigureAwait(false);
            foreach (var (stream, (first, count)) in GroupByStream(pointers))
            {
                var followers = GetFollowers(stream);
                if (followers.Length > 0)
                {
                    await DeliverAsync(stream, first, count, followers, cancellationToken).ConfigureAwait(false);
                }
            }

            if (pointers.Count < PointerPage)
            {
                return;
            }

            from = pointers[^1].Position + 1;
        }
    }

    /// <summary>The events of one stream the page points at, read once and handed to every follower of the stream.</summary>
    private async Task DeliverAsync(string stream, long first, int count, StreamFollowing[] followers, CancellationToken cancellationToken)
    {
        StreamSlice? page;
        try
        {
            page = await _store.ReadAsync(stream, Direction.Forwards, first, count, cancellationToken).ConfigureAwait(false);
        }
        catch (StreamDeletedException deleted)
        {
            // The stream's followers are told, as their own read would have told them; the
            // other streams' followers are not concerned.
            foreach (var following in followers)
            {
                Leave(following);
                following.Fail(deleted);
            }

            return;
        }

        if (page is null)
        {
            return;
        }

        foreach (var record in page.Events)
        {
            foreach (var following in followers)
            {
                if (!following.Lapsed && !following.TryDeliver(record))
                {
                    Leave(following);
                    following.Lapse();
                }
            }
        }
    }

    /// <summary>The first revision and the count of each stream's pointers, in the order the streams first appear.</summary>
    private static Dictionary<string, (long First, int Count)> GroupByStream(IReadOnlyList<EventPointer> pointers)
    {
        var groups = new Dictionary<string, (long First, int Count)>(StringComparer.Ordinal);
        foreach (var pointer in pointers)
        {
            groups[pointer.Stream] = groups.TryGetValue(pointer.Stream, out var group)
                ? (Math.Min(group.First, pointer.Revision), group.Count + 1)
                : (pointer.Revision, 1);
        }

        return groups;
    }

    private bool HasFollowers()
    {
        lock (_gate)
        {
            return _followers.Count > 0;
        }
    }

    private StreamFollowing[] GetFollowers(string stream)
    {
        lock (_gate)
        {
            return _followers.TryGetValue(stream, out var followers) ? [.. followers] : [];
        }
    }

    private void FailEveryone(Exception failure)
    {
        StreamFollowing[] everyone;
        lock (_gate)
        {
            everyone = [.. _followers.Values.SelectMany(followers => followers)];
            _followers.Clear();
        }

        foreach (var following in everyone)
        {
            following.Fail(failure);
        }
    }
}
