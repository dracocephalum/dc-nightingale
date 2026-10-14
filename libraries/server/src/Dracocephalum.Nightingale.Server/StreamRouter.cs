namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Hands the events of streams to the subscriptions that follow them, reading each advance of the
/// tail once for all of them rather than once per subscription. An advance costs one read of
/// where the new events are, their stream, revision, type and correlation id without their
/// bodies, matched in process against every followed stream, category, type and correlation, and
/// against the streams' and categories' listings for an event that begins its stream, and one read of the events of each
/// stream that matched something; when <c>$all</c> is followed, one read of the range with its
/// bodies serves everyone instead. So the cost follows the events, not the subscriptions, nor the
/// number of names followed. Names are matched with the comparer the backend compares them by,
/// so that a name matches live as it matches on a read; the category of a stream is the one rule
/// the store's category column computes, <see cref="StreamNames.CategoryOf"/>. One router per
/// tenant's store: a stream name is per tenant, and under a sequence per tenant so is the tail.
/// The router starts at the head as it stands and routes every range the tail advances through;
/// a subscription follows before it reads for the catch-up, so an event the catch-up does not see
/// is one the router brings. A plain stream's follower delivers only in revision order,
/// re-reading its stream when what the router brings is ahead of what it has delivered, which an
/// append whose number landed above a gap can do; a follower by position skips what it has
/// delivered already.
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
    /// <summary>How many pointers, or events under <c>$all</c>, one read of an advanced range asks for.</summary>
    public const int PointerPage = 512;

    /// <summary>How many events a follower may leave unread before it lapses.</summary>
    public const int FollowerBuffer = 512;

    private readonly IStreamStore _store;
    private readonly IStoreTail _tail;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<StreamFollowing>> _followers;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _routing;
    private long _routed;
    private int _correlationsFollowed;
    private Exception? _failure;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="StreamRouter"/> class and starts routing from the head as it stands.</summary>
    /// <param name="store">The store of the tenant whose streams are routed.</param>
    /// <param name="tail">The tail of the sequence the store's positions are numbered in.</param>
    /// <param name="names">How the backend compares names, which the followed names are matched with.</param>
    public StreamRouter(IStreamStore store, IStoreTail tail, IEqualityComparer<string> names)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(names);
        _store = store;
        _tail = tail;
        _followers = new Dictionary<string, List<StreamFollowing>>(names);
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
    /// <param name="stream">The stream name: a plain stream, <c>$all</c> or a virtual stream.</param>
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
                _correlationsFollowed += IsCorrelation(stream) ? 1 : 0;
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
                _correlationsFollowed -= IsCorrelation(following.Stream) ? 1 : 0;
            }
        }
    }

    private static bool IsCorrelation(string stream) => stream.StartsWith(StreamNames.CorrelationPrefix, StringComparison.Ordinal);

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

    /// <summary>
    /// Routes the events at positions from one up to another, both inclusive: the range itself,
    /// page by page, when <c>$all</c> is followed, since everyone's events are in it; otherwise
    /// its pointers, page by page, then the events of each stream that any of them matched.
    /// Nothing when nobody follows.
    /// </summary>
    private async Task RouteRangeAsync(long from, long head, CancellationToken cancellationToken)
    {
        var (any, all) = DescribeFollowed();
        if (!any)
        {
            return;
        }

        while (from <= head)
        {
            int count;
            long last;
            if (all)
            {
                var page = await _store.ReadAllAsync(Direction.Forwards, from, head, PointerPage, cancellationToken).ConfigureAwait(false);
                foreach (var record in page)
                {
                    Deliver(record, Match(record));
                }

                count = page.Count;
                last = count > 0 ? page[^1].Position : head;
            }
            else
            {
                var pointers = await _store.ReadPointersAsync(from, head, PointerPage, cancellationToken).ConfigureAwait(false);
                foreach (var (stream, (first, matched)) in GroupMatched(pointers))
                {
                    await DeliverStreamAsync(stream, first, matched, cancellationToken).ConfigureAwait(false);
                }

                count = pointers.Count;
                last = count > 0 ? pointers[^1].Position : head;
            }

            if (count < PointerPage)
            {
                return;
            }

            from = last + 1;
        }
    }

    /// <summary>The events of one stream the page points at, from the first revision pointed at, read once and each handed to whoever it matches.</summary>
    private async Task DeliverStreamAsync(string stream, long first, int count, CancellationToken cancellationToken)
    {
        StreamSlice? page;
        try
        {
            // As stored: a category's or a type's followers get these events too, and a stream's
            // metadata hides nothing from them; a stream's own followers are live, as the
            // reference's are, past what a catch-up read applied the metadata to.
            page = await _store.ReadAsStoredAsync(stream, first, count, cancellationToken).ConfigureAwait(false);
        }
        catch (StreamDeletedException deleted)
        {
            // The stream's own followers are told, as their own read would have told them; a
            // category's or a type's followers are not concerned with one stream.
            foreach (var following in GetFollowers(stream))
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
            Deliver(record, Match(record));
        }
    }

    private void Deliver(EventRecord record, List<StreamFollowing> followers)
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

    /// <summary>
    /// Everyone an event is for: the followers of its stream, of its category, of its type, of
    /// <c>$all</c>, of the data events when its type is not a system one, of its correlation when
    /// it has one, and, when it begins its stream, of <c>$streams</c> and of its category's
    /// streams.
    /// </summary>
    private List<StreamFollowing> Match(EventRecord record)
    {
        var matched = new List<StreamFollowing>();
        lock (_gate)
        {
            foreach (var name in GetNames(record.Stream, record.Revision, record.Type, GetCorrelationId(record)))
            {
                Collect(name, matched);
            }
        }

        return matched;
    }

    /// <summary>The names an event is followed under; the correlation's only while some correlation is followed, so an event's id is not looked at for nothing.</summary>
    private IEnumerable<string> GetNames(string stream, long revision, string type, string? correlationId)
    {
        var category = StreamNames.CategoryOf(stream);
        yield return stream;
        yield return StreamNames.CategoryPrefix + category;
        yield return StreamNames.EventTypePrefix + type;
        yield return StreamNames.All;
        if (!StreamNames.IsSystemEventType(type))
        {
            yield return StreamNames.DataEvents;
        }

        if (correlationId is not null && _correlationsFollowed > 0)
        {
            yield return StreamNames.CorrelationPrefix + correlationId;
        }

        if (revision == 0)
        {
            yield return StreamNames.Streams;
            yield return StreamNames.CategoryStreamsPrefix + category;
        }
    }

    private static string? GetCorrelationId(EventRecord record) =>
        record.Metadata.TryGetPropertyValue(MetadataKeys.CorrelationId, out var value) && value is System.Text.Json.Nodes.JsonValue text && text.TryGetValue<string>(out var id) ? id : null;

    /// <summary>The first revision pointed at and how many pointers, of each stream any pointer of which is somebody's, in the order the streams first appear.</summary>
    private Dictionary<string, (long First, int Count)> GroupMatched(IReadOnlyList<EventPointer> pointers)
    {
        var groups = new Dictionary<string, (long First, int Count)>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var pointer in pointers)
            {
                if (groups.TryGetValue(pointer.Stream, out var group))
                {
                    groups[pointer.Stream] = (Math.Min(group.First, pointer.Revision), group.Count + 1);
                }
                else if (GetNames(pointer.Stream, pointer.Revision, pointer.Type, pointer.CorrelationId).Any(IsFollowed))
                {
                    groups[pointer.Stream] = (pointer.Revision, 1);
                }
            }
        }

        return groups;
    }

    private void Collect(string name, List<StreamFollowing> into)
    {
        if (_followers.TryGetValue(name, out var followers))
        {
            into.AddRange(followers);
        }
    }

    private bool IsFollowed(string name) => _followers.ContainsKey(name);

    /// <summary>
    /// Whether anyone follows anything, and whether anyone follows <c>$all</c> or the data events,
    /// which are nearly all of it: either is served by one read of the range with its bodies.
    /// </summary>
    private (bool Any, bool All) DescribeFollowed()
    {
        lock (_gate)
        {
            return (_followers.Count > 0, _followers.ContainsKey(StreamNames.All) || _followers.ContainsKey(StreamNames.DataEvents));
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
