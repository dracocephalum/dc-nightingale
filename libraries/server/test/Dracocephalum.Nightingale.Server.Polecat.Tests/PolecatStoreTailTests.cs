using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The tail's loop, with the store's daemon stood in for by two delegates: it publishes the mark
/// as it moves, ends quietly when stopped, and when it ends for any other reason says so to
/// everyone waiting on the head instead of leaving them waiting.
/// </summary>
public sealed class PolecatStoreTailTests
{
    [Fact]
    public async Task Follow_ShouldPublishTheMarkAsItMovesAndEndQuietlyWhenStopped()
    {
        // Arrange: the mark moves to 3, then nothing more happens until the tail is stopped.
        await using var sut = new PolecatStoreTail(null!, null!, NullLoggerFactory.Instance);
        using var stopping = new CancellationTokenSource();
        var mark = 0L;
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task WaitBeyond(long beyond, CancellationToken cancellationToken) =>
            beyond < 3 ? moved.Task.WaitAsync(cancellationToken) : Task.Delay(Timeout.Infinite, cancellationToken);

        // Act
        var following = sut.FollowAsync(WaitBeyond, () => mark, stopping.Token);
        var waiting = sut.WaitForAdvanceAsync(0, TestContext.Current.CancellationToken).AsTask();
        mark = 3;
        moved.SetResult();
        var head = await waiting;
        var waitingForMore = sut.WaitForAdvanceAsync(3, stopping.Token).AsTask();
        await stopping.CancelAsync();
        await following;

        // Assert: stopping is not a failure; whoever waited is cancelled by its own token.
        head.ShouldBe(3);
        sut.Head.ShouldBe(3);
        await Should.ThrowAsync<OperationCanceledException>(() => waitingForMore);
    }

    [Theory]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Follow_WhenTheWaitEndsWithoutTheTailBeingStopped_ShouldFailEveryoneWaitingWithTheCause(Type thrown)
    {
        // Arrange: the daemon goes away under the tail, which nobody stopped. A cancellation
        // that is not the tail's own is the same thing.
        await using var sut = new PolecatStoreTail(null!, null!, NullLoggerFactory.Instance);
        var cause = thrown == typeof(ObjectDisposedException) ? new ObjectDisposedException("tracker") : (Exception)Activator.CreateInstance(thrown)!;
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task WaitBeyond(long beyond, CancellationToken cancellationToken) => gone.Task;

        // Act: one waiter is already waiting when it happens, another arrives after.
        var following = sut.FollowAsync(WaitBeyond, () => 0, CancellationToken.None);
        var already = sut.WaitForAdvanceAsync(0, TestContext.Current.CancellationToken).AsTask();
        gone.SetException(cause);
        await following;
        var before = await Should.ThrowAsync<InvalidOperationException>(() => already);
        var after = await Should.ThrowAsync<InvalidOperationException>(() => sut.WaitForAdvanceAsync(0, TestContext.Current.CancellationToken).AsTask());

        // Assert
        before.InnerException.ShouldBeSameAs(cause);
        before.Message.ShouldContain("stopped following the store's head");
        after.ShouldBeSameAs(before);
    }

    [Fact]
    public async Task Follow_WhenTheWaitRunsOutOnAQuietStore_ShouldGoOnFollowing()
    {
        // Arrange: the store's tracker gives up its wait after a minute without an append, which
        // on a quiet store is every minute. Twice nothing has moved; then the mark is at 4.
        await using var sut = new PolecatStoreTail(null!, null!, NullLoggerFactory.Instance);
        using var stopping = new CancellationTokenSource();
        var waits = 0;
        Task WaitBeyond(long beyond, CancellationToken cancellationToken) => Interlocked.Increment(ref waits) switch
        {
            1 or 2 => Task.FromException(new TimeoutException("Shard HighWaterMark did not reach sequence number 1 in the time allowed")),
            3 => Task.CompletedTask,
            _ => Task.Delay(Timeout.Infinite, cancellationToken),
        };

        // Act
        var following = sut.FollowAsync(WaitBeyond, () => Volatile.Read(ref waits) >= 3 ? 4 : 0, stopping.Token);
        var head = await sut.WaitForAdvanceAsync(0, TestContext.Current.CancellationToken);
        await stopping.CancelAsync();
        await following;

        // Assert: a quiet minute is not a failure, and what comes after it is delivered.
        head.ShouldBe(4);
    }

    [Fact]
    public async Task WaitForAdvance_WhenTheHeadIsAlreadyBeyond_ShouldAnswerEvenAfterTheTailFailed()
    {
        // Arrange: the head reached 5 before the tail stopped following.
        await using var sut = new PolecatStoreTail(null!, null!, NullLoggerFactory.Instance);
        var calls = 0;
        Task WaitBeyond(long beyond, CancellationToken cancellationToken) =>
            calls++ == 0 ? Task.CompletedTask : Task.FromException(new ObjectDisposedException("tracker"));
        await sut.FollowAsync(WaitBeyond, () => 5, CancellationToken.None);

        // Act: what is known is still true; only what will never come is refused.
        var known = await sut.WaitForAdvanceAsync(2, TestContext.Current.CancellationToken);

        // Assert
        known.ShouldBe(5);
        await Should.ThrowAsync<InvalidOperationException>(() => sut.WaitForAdvanceAsync(5, TestContext.Current.CancellationToken).AsTask());
    }
}
