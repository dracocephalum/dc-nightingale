using System.Runtime.CompilerServices;

using Dracocephalum.Nightingale;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// An endless, ordered feed of a stream's events from a position: the pages the catch-up phase
/// reads, then whatever each advance of the tail brings. It is what a persistent-subscription
/// group reads from; the catch-up subscriptions of the streams service keep their own loops,
/// because they also narrate where the head is, but share the wait an ordinal feed does.
/// </summary>
internal static class EventSource
{
    /// <summary>
    /// The most positions one scan of <c>$all</c> under a filter examines: what bounds a call over
    /// a stretch the filter keeps nothing of, and what a checkpoint advances by at most at a time.
    /// </summary>
    public const int ScanWindow = 4096;

    /// <summary>Follows a stream, <c>$all</c> or a virtual stream from a position, inclusive, forever.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tail">The tail.</param>
    /// <param name="router">The router a plain stream's events come through once the feed has caught up.</param>
    /// <param name="stream">The stream name.</param>
    /// <param name="from">Where to begin, inclusive: a revision for a stream, a position otherwise, an ordinal under ordinal numbering.</param>
    /// <param name="pageSize">How many events one store call asks for.</param>
    /// <param name="numbering">How <paramref name="from"/> and the feed are numbered; ordinal only for a virtual stream.</param>
    /// <param name="filter">What to keep of <c>$all</c>; <see langword="null"/> for everything, and for any other stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events, in order, without end; under a filter, the stretches examined that held none for it as well.</returns>
    public static IAsyncEnumerable<FeedItem> FollowAsync(IStreamStore store, IStoreTail tail, StreamRouter router, string stream, long from, int pageSize, Numbering numbering, EventFilter? filter, CancellationToken cancellationToken)
    {
        if (stream == StreamNames.All)
        {
            return filter is null
                ? FollowByPosition(store, tail, router, null, from, pageSize, cancellationToken)
                : FollowAllFiltered(store, tail, router, filter, from, pageSize, cancellationToken);
        }

        if (StreamNames.TryParseVirtual(stream, out var virtualStream))
        {
            return numbering == Numbering.Ordinal
                ? FollowOrdinal(store, tail, virtualStream, from, pageSize, cancellationToken)
                : FollowByPosition(store, tail, router, virtualStream, from, pageSize, cancellationToken);
        }

        return FollowStream(store, router, stream, from, pageSize, cancellationToken);
    }

    /// <summary>
    /// What an ordinal reader does once it has read every numbered row: if the sequencer had
    /// passed the head the reader last observed, wait for the tail to move beyond it; otherwise
    /// wait for the numbering to reach that head, because the rows the reader is waiting for are
    /// committed and only not numbered yet, and look again when it has.
    /// </summary>
    /// <remarks>
    /// How far the sequencer had come is taken before the read, never after it. Taken after, the
    /// answer can be "through the head" for a row the sequencer numbered between the read and
    /// the question: the read missed it, the reader then waits for the tail to move, and on a
    /// store nothing more is appended to it waits for good with the row never delivered.
    /// </remarks>
    /// <param name="tail">The tail.</param>
    /// <param name="numberedBeforeRead">How far the sequencer had numbered when the reader began the read it has just finished.</param>
    /// <param name="observed">The head the reader last took from the tail.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head to observe next: a new one after an advance, the same one once it is numbered.</returns>
    public static async Task<long> AwaitNumberingAsync(IStoreTail tail, long numberedBeforeRead, long observed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tail);
        if (numberedBeforeRead >= observed)
        {
            return await tail.WaitForAdvanceAsync(observed, cancellationToken).ConfigureAwait(false);
        }

        await tail.WaitForNumberedAsync(observed, cancellationToken).ConfigureAwait(false);
        return observed;
    }

    /// <summary>
    /// A plain stream: followed at the router before the catch-up reads, so an event the reads do
    /// not see yet is one the router brings; then what the router brings, in revision order only.
    /// What is behind the next revision was read already; what is ahead of it means the stream
    /// is read again from the next revision, as it is when the follower has lapsed.
    /// </summary>
    private static async IAsyncEnumerable<FeedItem> FollowStream(IStreamStore store, StreamRouter router, string stream, long next, int pageSize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            using var following = router.Follow(stream);
            while (true)
            {
                var page = await store.ReadAsync(stream, Direction.Forwards, next, pageSize, cancellationToken).ConfigureAwait(false);
                if (page is null || page.Events.Count == 0)
                {
                    break;
                }

                foreach (var record in page.Events)
                {
                    yield return FeedItem.Of(record);
                }

                next = page.Events[^1].Revision + 1;
                if (page.Events.Count < pageSize)
                {
                    break;
                }
            }

            var ahead = false;
            while (!ahead && await following.Events.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
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

                    yield return FeedItem.Of(record);
                    next = record.Revision + 1;
                }
            }
        }
    }

    /// <summary>
    /// <c>$all</c> or a virtual stream by position: followed at the router before the catch-up
    /// reads up to the head as it stands, then what the router brings, skipping what the catch-up
    /// delivered already. A lapse means catching up again from where the delivery stands.
    /// </summary>
    private static async IAsyncEnumerable<FeedItem> FollowByPosition(IStreamStore store, IStoreTail tail, StreamRouter router, VirtualStreamName? virtualStream, long next, int pageSize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            using var following = router.Follow(virtualStream?.Name ?? StreamNames.All);
            var head = tail.Head;
            while (head > 0 && next <= head)
            {
                var page = virtualStream is { } name
                    ? await store.ReadVirtualAsync(name, Direction.Forwards, next, head, pageSize, cancellationToken).ConfigureAwait(false)
                    : await store.ReadAllAsync(Direction.Forwards, next, head, pageSize, cancellationToken).ConfigureAwait(false);
                foreach (var record in page)
                {
                    yield return FeedItem.Of(record);
                }

                next = page.Count < pageSize ? head + 1 : page[^1].Position + 1;
            }

            while (await following.Events.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (following.Events.TryRead(out var record))
                {
                    if (record.Position < next)
                    {
                        continue;
                    }

                    yield return FeedItem.Of(record);
                    next = record.Position + 1;
                }
            }
        }
    }

    /// <summary>
    /// <c>$all</c> under a filter: the shape of <see cref="FollowByPosition"/>, catching up through
    /// the store's scan a stretch at a time and keeping, of what the router brings, what the filter
    /// keeps. A stretch examined past the last event kept, and what the router brought that the
    /// filter did not keep, are reported as examined, after the events before them, so a group's
    /// checkpoint can advance over them.
    /// </summary>
    private static async IAsyncEnumerable<FeedItem> FollowAllFiltered(IStreamStore store, IStoreTail tail, StreamRouter router, EventFilter filter, long next, int pageSize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            using var following = router.Follow(StreamNames.All);
            var head = tail.Head;
            while (head > 0 && next <= head)
            {
                var slice = await store.ScanAllAsync(filter, Direction.Forwards, next, head, ScanWindow, pageSize, cancellationToken).ConfigureAwait(false);
                foreach (var record in slice.Events)
                {
                    yield return FeedItem.Of(record);
                }

                if (slice.Events.Count == 0 || slice.Through > slice.Events[^1].Position)
                {
                    yield return FeedItem.Scanned(slice.Through);
                }

                next = slice.Through + 1;
            }

            while (await following.Events.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var skippedThrough = -1L;
                while (following.Events.TryRead(out var record))
                {
                    if (record.Position < next)
                    {
                        continue;
                    }

                    next = record.Position + 1;
                    if (!filter.Matches(record.Stream, record.Type))
                    {
                        skippedThrough = record.Position;
                        continue;
                    }

                    // What was skipped before this event is reported first, so a checkpoint can
                    // stand there while the event is in flight.
                    if (skippedThrough >= 0)
                    {
                        yield return FeedItem.Scanned(skippedThrough);
                        skippedThrough = -1;
                    }

                    yield return FeedItem.Of(record);
                }

                if (skippedThrough >= 0)
                {
                    yield return FeedItem.Scanned(skippedThrough);
                }
            }
        }
    }

    private static async IAsyncEnumerable<FeedItem> FollowOrdinal(IStreamStore store, IStoreTail tail, VirtualStreamName virtualStream, long next, int pageSize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var observed = tail.Head;
        while (true)
        {
            var numbered = tail.Numbered;
            while (true)
            {
                var page = await store.ReadByOrdinalAsync(virtualStream, Direction.Forwards, next, pageSize, cancellationToken).ConfigureAwait(false);
                foreach (var record in page)
                {
                    yield return FeedItem.Of(record);
                    next = (record.Ordinal ?? throw new InvalidOperationException("The store returned an event without its ordinal from an ordinal read.")) + 1;
                }

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            observed = await AwaitNumberingAsync(tail, numbered, observed, cancellationToken).ConfigureAwait(false);
        }
    }
}
