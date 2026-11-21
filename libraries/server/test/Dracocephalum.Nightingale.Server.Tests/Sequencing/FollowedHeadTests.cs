using Dracocephalum.Nightingale.Server.Sequencing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>A head that is followed: it only moves forward, wakes whoever waits for a mark it reaches, and tells its waiters why when it is stopped or has failed.</summary>
public sealed class FollowedHeadTests
{
    private readonly FollowedHead _sut = new();

    [Fact]
    public void Publish_ShouldMoveTheHeadForwardOnlyAndSayWhetherItMoved()
    {
        // Act
        var forward = _sut.Publish(5);
        var same = _sut.Publish(5);
        var back = _sut.Publish(3);

        // Assert
        (forward, same, back).ShouldBe((true, false, false));
        _sut.Head.ShouldBe(5);
    }

    [Fact]
    public async Task WaitForReachAsync_WhenTheMarkIsAlreadyReached_ShouldGiveTheHeadAtOnce()
    {
        // Arrange
        _sut.Publish(5);

        // Act
        var reached = _sut.WaitForReachAsync(3, TestContext.Current.CancellationToken);

        // Assert
        reached.IsCompletedSuccessfully.ShouldBeTrue();
        (await reached).ShouldBe(5);
    }

    [Fact]
    public async Task WaitForAdvanceAsync_ShouldWaitUntilTheHeadPassesWhatWasSeen()
    {
        // Arrange
        _sut.Publish(5);
        var waiting = _sut.WaitForAdvanceAsync(5, TestContext.Current.CancellationToken).AsTask();
        var stillWaiting = waiting.IsCompleted;

        // Act
        _sut.Publish(7);

        // Assert
        stillWaiting.ShouldBeFalse();
        (await waiting).ShouldBe(7);
    }

    [Fact]
    public async Task Stop_ShouldTellWaitersWhyNowAndForAsLongAsItLasts()
    {
        // Arrange
        var reason = new InvalidOperationException("The numbering is stopped.");
        var waiting = _sut.WaitForReachAsync(1, TestContext.Current.CancellationToken).AsTask();

        // Act
        _sut.Stop(reason);

        // Assert
        (await Should.ThrowAsync<InvalidOperationException>(() => waiting)).ShouldBeSameAs(reason);
        (await Should.ThrowAsync<InvalidOperationException>(async () => await _sut.WaitForReachAsync(1, TestContext.Current.CancellationToken))).ShouldBeSameAs(reason);
        _sut.Stopped.ShouldBeSameAs(reason);
    }

    [Fact]
    public async Task Resume_ShouldLetWaitersWaitAgain()
    {
        // Arrange
        _sut.Stop(new InvalidOperationException("The numbering is stopped."));

        // Act
        _sut.Resume();
        var waiting = _sut.WaitForReachAsync(1, TestContext.Current.CancellationToken).AsTask();
        _sut.Publish(1);

        // Assert
        _sut.Stopped.ShouldBeNull();
        (await waiting).ShouldBe(1);
    }

    [Fact]
    public async Task Fail_ShouldEndEveryWaitNowAndLaterAndKeepWhatIsKnown()
    {
        // Arrange
        var failure = new InvalidOperationException("The head is no longer followed.");
        _sut.Publish(5);
        var waiting = _sut.WaitForReachAsync(6, TestContext.Current.CancellationToken).AsTask();

        // Act
        _sut.Fail(failure);

        // Assert: a mark reached before the failure is still answered.
        (await Should.ThrowAsync<InvalidOperationException>(() => waiting)).ShouldBeSameAs(failure);
        (await Should.ThrowAsync<InvalidOperationException>(async () => await _sut.WaitForReachAsync(6, TestContext.Current.CancellationToken))).ShouldBeSameAs(failure);
        (await _sut.WaitForReachAsync(5, TestContext.Current.CancellationToken)).ShouldBe(5);
    }

    [Fact]
    public async Task WaitForReachAsync_WhenCancelled_ShouldStopWaiting()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = _sut.WaitForReachAsync(1, cancellation.Token).AsTask();

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
    }
}
