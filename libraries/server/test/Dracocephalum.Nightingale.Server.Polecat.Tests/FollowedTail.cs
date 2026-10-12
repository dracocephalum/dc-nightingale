namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>A tail a test moves by hand, over the backend's own followed marks: waiters wake exactly as they do on the real one.</summary>
internal sealed class FollowedTail : IStoreTail
{
    public FollowedHead Followed { get; } = new();

    public FollowedHead Numbering { get; } = new();

    public long Head => Followed.Head;

    public long Numbered => Numbering.Head;

    public ValueTask<long> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Head);

    public ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken) => Followed.WaitForAdvanceAsync(beyond, cancellationToken);

    public ValueTask<long> WaitForNumberedAsync(long through, CancellationToken cancellationToken) => Numbering.WaitForReachAsync(through, cancellationToken);
}
