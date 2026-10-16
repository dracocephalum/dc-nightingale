using Dracocephalum.Nightingale.Server.Sequencing;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>A tail a test moves by hand, over followed heads: waiters wake exactly as they do on the real one.</summary>
internal sealed class FollowedTail : IStoreTail
{
    public FollowedHead Followed { get; } = new();

    public FollowedHead Numbering { get; } = new();

    public long Head => Followed.Head;

    public long Numbered => Numbering.Head;

    public OrdinalSequencerException? NumberingStopped => Numbering.Stopped as OrdinalSequencerException;

    public ValueTask<long> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Head);

    public ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken) => Followed.WaitForAdvanceAsync(beyond, cancellationToken);

    public ValueTask<long> WaitForNumberedAsync(long through, CancellationToken cancellationToken) => Numbering.WaitForReachAsync(through, cancellationToken);
}
