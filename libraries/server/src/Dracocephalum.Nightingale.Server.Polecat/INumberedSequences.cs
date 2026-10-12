namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The sequences the sequencer numbers and the way it waits for any of them to move: the one
/// shared sequence, or a sequence per tenant under tenant partitioning, where the tenants are
/// those the gateway keeps, since the store onboards a tenant on the gateway's first append for
/// it and never without.
/// </summary>
internal interface INumberedSequences
{
    /// <summary>Gets a count that grows whenever any sequence's head moves; what <see cref="WaitForAnyAdvanceAsync"/> is asked against.</summary>
    long Advances { get; }

    /// <summary>Lists the sequences as they are now: every one there is, for the sequencer that numbers them all.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sequences.</returns>
    ValueTask<IReadOnlyList<NumberedSequence>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Lists the sequences this process follows, without asking the store: those something in the process waits on, for the follower that keeps their numbering current.</summary>
    /// <returns>The sequences followed here.</returns>
    IReadOnlyList<NumberedSequence> ListFollowed();

    /// <summary>Waits until any sequence's head has moved since a count was read.</summary>
    /// <param name="seen">The count the caller has already acted on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count, greater than <paramref name="seen"/>.</returns>
    Task<long> WaitForAnyAdvanceAsync(long seen, CancellationToken cancellationToken);
}
