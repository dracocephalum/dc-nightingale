using System.Runtime.CompilerServices;

using Dracocephalum.Nightingale;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// An endless, ordered feed of a stream's events from a position: the pages the catch-up phase
/// reads, then whatever each advance of the tail brings. It is what a persistent-subscription
/// group reads from; the catch-up subscriptions of the streams service keep their own loops,
/// because they also narrate where the head is.
/// </summary>
internal static class EventSource
{
    /// <summary>Follows a stream, <c>$all</c> or a virtual stream from a position, inclusive, forever.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tail">The tail.</param>
    /// <param name="stream">The stream name.</param>
    /// <param name="from">Where to begin, inclusive: a revision for a stream, a position otherwise.</param>
    /// <param name="pageSize">How many events one store call asks for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events, in order, without end.</returns>
    public static IAsyncEnumerable<EventRecord> FollowAsync(IStreamStore store, IStoreTail tail, string stream, long from, int pageSize, CancellationToken cancellationToken)
    {
        if (stream == StreamNames.All)
        {
            return FollowAll(store, tail, null, from, pageSize, cancellationToken);
        }

        if (StreamNames.TryParseVirtual(stream, out var virtualStream))
        {
            return FollowAll(store, tail, virtualStream, from, pageSize, cancellationToken);
        }

        return FollowStream(store, tail, stream, from, pageSize, cancellationToken);
    }

    private static async IAsyncEnumerable<EventRecord> FollowStream(IStreamStore store, IStoreTail tail, string stream, long next, int pageSize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var observed = tail.Head;
            var page = await store.ReadAsync(stream, Direction.Forwards, next, pageSize, cancellationToken).ConfigureAwait(false);
            if (page is not null && page.Events.Count > 0)
            {
                foreach (var record in page.Events)
                {
                    yield return record;
                }

                next = page.Events[^1].Revision + 1;
                if (page.Events.Count == pageSize)
                {
                    continue;
                }
            }

            await tail.WaitForAdvanceAsync(observed, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<EventRecord> FollowAll(IStreamStore store, IStoreTail tail, VirtualStreamName? virtualStream, long next, int pageSize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var head = tail.Head;
        while (true)
        {
            while (head > 0 && next <= head)
            {
                var page = virtualStream is { } name
                    ? await store.ReadVirtualAsync(name, Direction.Forwards, next, head, pageSize, cancellationToken).ConfigureAwait(false)
                    : await store.ReadAllAsync(Direction.Forwards, next, head, pageSize, cancellationToken).ConfigureAwait(false);
                foreach (var record in page)
                {
                    yield return record;
                }

                next = page.Count < pageSize ? head + 1 : page[^1].Position + 1;
            }

            head = await tail.WaitForAdvanceAsync(head, cancellationToken).ConfigureAwait(false);
        }
    }
}
