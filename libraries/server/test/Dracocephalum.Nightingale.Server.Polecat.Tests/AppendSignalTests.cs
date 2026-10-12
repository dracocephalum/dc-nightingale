using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The signal between an append and the tail: a wait ends at a pulse or when its time is up, a
/// pulse nobody was waiting for ends the next wait and only that one, and stopping cancels.
/// </summary>
public sealed class AppendSignalTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly AppendSignal _sut;

    public AppendSignalTests()
    {
        _sut = new AppendSignal(_time);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task Wait_WhenPulsed_ShouldEndWithoutTheTimeHavingPassed()
    {
        // Arrange
        var waiting = _sut.WaitAsync(Interval, TestContext.Current.CancellationToken);
        var before = waiting.IsCompleted;

        // Act
        _sut.Pulse();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        before.ShouldBeFalse();
    }

    [Fact]
    public async Task Wait_WhenNothingIsPulsed_ShouldEndWhenTheTimeIsUp()
    {
        // Arrange
        var waiting = _sut.WaitAsync(Interval, TestContext.Current.CancellationToken);
        _time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        var early = waiting.IsCompleted;

        // Act
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        early.ShouldBeFalse();
    }

    [Fact]
    public void Wait_AfterAPulseNobodyWaitedFor_ShouldEndAtOnceAndOnlyOnce()
    {
        // Arrange: two appends commit while the tail is looking, not waiting.
        _sut.Pulse();
        _sut.Pulse();

        // Act
        var first = _sut.WaitAsync(Interval, TestContext.Current.CancellationToken);
        var second = _sut.WaitAsync(Interval, TestContext.Current.CancellationToken);

        // Assert: both pulses are taken by the one look that follows the first wait.
        first.IsCompletedSuccessfully.ShouldBeTrue();
        second.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Stop_ShouldEndTheCurrentWaitAtOnceAndMakeEveryLaterOneAMoment()
    {
        // Arrange: a wait of the interval is under way.
        var waiting = _sut.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        // Act
        _sut.Stop();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var later = _sut.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        var atOnce = later.IsCompleted;
        _time.Advance(AppendSignal.StoppingWait);
        await later.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert: no time passed for the first; the later one lasted the moment, not the interval,
        // and not nothing either, so an agent whose stop flag is not yet set does not spin.
        atOnce.ShouldBeFalse();
    }

    [Fact]
    public async Task Wait_WhenCancelled_ShouldBeCancelled()
    {
        // Arrange
        using var stopping = new CancellationTokenSource();
        var waiting = _sut.WaitAsync(Interval, stopping.Token);

        // Act
        await stopping.CancelAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
    }
}
