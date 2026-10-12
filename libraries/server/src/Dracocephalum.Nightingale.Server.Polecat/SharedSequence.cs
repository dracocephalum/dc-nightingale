namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>The one shared sequence, whose head is the count: it moves when the head does.</summary>
/// <param name="tail">The tail of the process.</param>
/// <param name="numbering">Where how far the sequence is numbered is published for the process.</param>
internal sealed class SharedSequence(IStoreTail tail, FollowedHead numbering) : INumberedSequences
{
    private readonly IReadOnlyList<NumberedSequence> _sequences = [new(null, tail, numbering)];

    /// <inheritdoc/>
    public long Advances => tail.Head;

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<NumberedSequence>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_sequences);

    /// <inheritdoc/>
    public IReadOnlyList<NumberedSequence> ListFollowed() => _sequences;

    /// <inheritdoc/>
    public async Task<long> WaitForAnyAdvanceAsync(long seen, CancellationToken cancellationToken) => await tail.WaitForAdvanceAsync(seen, cancellationToken).ConfigureAwait(false);
}
