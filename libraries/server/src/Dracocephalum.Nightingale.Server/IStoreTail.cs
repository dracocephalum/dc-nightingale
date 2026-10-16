namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The store's head as it moves: the highest position up to which every event is committed, and
/// a way to wait for it to move; and, on a store with ordinals, how far behind it the sequencer
/// has numbered, and a way to wait for that. One tail serves every subscription in the process,
/// so liveness costs the store one poll per interval rather than one per subscriber. A backend
/// registers its implementation beside its <see cref="IStreamStore"/>.
/// </summary>
public interface IStoreTail
{
    /// <summary>
    /// Gets the position up to which every event has its ordinals, as this process knows it: never
    /// ahead of the sequencer, so a subscription under ordinal numbering can tell "nothing new for
    /// this stream" from "not numbered yet". 0 when nothing is, or the store has no ordinals.
    /// </summary>
    long Numbered { get; }

    /// <summary>Waits until the numbering has reached a position.</summary>
    /// <param name="through">The position the caller needs numbered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The position numbered through, at least <paramref name="through"/>.</returns>
    /// <exception cref="InvalidOperationException">The tail has stopped following the store, so the numbering will not advance; the cause is the inner exception.</exception>
    ValueTask<long> WaitForNumberedAsync(long through, CancellationToken cancellationToken);

    /// <summary>
    /// Gets why nothing is being numbered, while the ordinal sequencer is disabled or faulted for
    /// this sequence; <see langword="null"/> while it numbers, and on a store without ordinals. A
    /// wait on the numbering ends with it meanwhile, and a read by ordinal is refused with it.
    /// </summary>
    OrdinalSequencerException? NumberingStopped { get; }

    /// <summary>
    /// Gets the high-water mark: the position up to which every event is committed. 0 when the
    /// store has no events yet. Never the highest sequence number in the table, because a number
    /// is taken before its transaction commits and a lower one can become visible after a higher.
    /// </summary>
    long Head { get; }

    /// <summary>
    /// Brings the head up to date now, rather than at the next poll: advances it through every
    /// committed position that follows it without a gap, and returns it. A gap is a number taken
    /// by a transaction that has not committed, and the head stops before it until it does or
    /// the poller gives it up as abandoned. Used where "now" matters: a subscription from the end,
    /// a bounded read, so that an append that has returned is in what follows.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head, current as of the call.</returns>
    ValueTask<long> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Waits until the head is beyond a position.</summary>
    /// <param name="beyond">The position the caller has already seen.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head, greater than <paramref name="beyond"/>.</returns>
    /// <exception cref="InvalidOperationException">The tail has stopped following the store, so the head will not advance; the cause is the inner exception.</exception>
    ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken);
}
