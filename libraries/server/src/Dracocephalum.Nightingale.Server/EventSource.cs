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
    /// How long an ordinal feed waits between looks while the sequencer is behind the head it
    /// woke up for. Ordinals are assigned after commit, so an advance of the tail says events
    /// were committed, not that they are numbered yet; the feed looks again on this cadence
    /// until the sequencer has passed the head, then waits on the tail once more.
    /// </summary>
    public static readonly TimeSpan NumberingPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Follows a stream, <c>$all</c> or a virtual stream from a position, inclusive, forever.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tail">The tail.</param>
    /// <param name="stream">The stream name.</param>
    /// <param name="from">Where to begin, inclusive: a revision for a stream, a position otherwise, an ordinal under ordinal numbering.</param>
    /// <param name="pageSize">How many events one store call asks for.</param>
    /// <param name="numbering">How <paramref name="from"/> and the feed are numbered; ordinal only for a virtual stream.</param>
    /// <param name="timeProvider">The clock an ordinal feed paces its looks with.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events, in order, without end.</returns>
    public static IAsyncEnumerable<EventRecord> FollowAsync(IStreamStore store, IStoreTail tail, string stream, long from, int pageSize, Numbering numbering, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (stream == StreamNames.All)
        {
            return FollowAll(store, tail, null, from, pageSize, cancellationToken);
        }

        if (StreamNames.TryParseVirtual(stream, out var virtualStream))
        {
            return numbering == Numbering.Ordinal
                ? FollowOrdinal(store, tail, virtualStream, from, pageSize, timeProvider, cancellationToken)
                : FollowAll(store, tail, virtualStream, from, pageSize, cancellationToken);
        }

        return FollowStream(store, tail, stream, from, pageSize, cancellationToken);
    }

    /// <summary>
    /// What an ordinal reader does once it has read every numbered row: if the sequencer has passed
    /// the head the reader last observed, wait for the tail to move beyond it; otherwise look again
    /// after the poll interval, because the rows the reader is waiting for are committed and only
    /// not numbered yet.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="tail">The tail.</param>
    /// <param name="observed">The head the reader last took from the tail.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head to observe next: a new one after an advance, the same one after a poll.</returns>
    public static async Task<long> AwaitNumberingAsync(IStreamStore store, IStoreTail tail, long observed, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (await store.NumberedThroughAsync(cancellationToken).ConfigureAwait(false) >= observed)
        {
            return await tail.WaitForAdvanceAsync(observed, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(NumberingPollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        return observed;
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

    private static async IAsyncEnumerable<EventRecord> FollowOrdinal(IStreamStore store, IStoreTail tail, VirtualStreamName virtualStream, long next, int pageSize, TimeProvider timeProvider, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var observed = tail.Head;
        while (true)
        {
            while (true)
            {
                var page = await store.ReadByOrdinalAsync(virtualStream, Direction.Forwards, next, pageSize, cancellationToken).ConfigureAwait(false);
                foreach (var record in page)
                {
                    yield return record;
                    next = (record.Ordinal ?? throw new InvalidOperationException("The store returned an event without its ordinal from an ordinal read.")) + 1;
                }

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            observed = await AwaitNumberingAsync(store, tail, observed, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
