using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>
/// The follower that keeps a process's numbering current while another instance numbers, with
/// no database: when it reads the progress rows, what it publishes, and what a failed read costs.
/// </summary>
public sealed class OrdinalSequencingFollowerTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Started);
    private readonly FollowedTail _tail = new();
    private readonly FakeOrdinalSequencingProgressReader _progress = new();

    [Fact]
    public async Task Follow_WhileTheHeadIsPastTheNumbering_ShouldReadTheProgressAndPublishIt()
    {
        // Arrange: the head is at 20 and nothing is numbered as far as this process knows; the
        // row says the sequencer has reached 20.
        _tail.Followed.Publish(20);
        _progress.Holding(null, 20);
        var sut = Follower();

        // Act: one interval's worth of following; the read is the first thing it does.
        var following = sut.FollowAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var reached = await _tail.WaitForNumberedAsync(20, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await RunOutAsync(following, TimeSpan.FromSeconds(1));

        // Assert: published once read, then nothing to read until the head moves again.
        reached.ShouldBe(20);
        _tail.Numbered.ShouldBe(20);
        _progress.Reads.ShouldBe(1);
        _progress.LastAskedFor.ShouldBe([null]);
    }

    [Fact]
    public async Task Follow_WhileNothingIsBehind_ShouldWaitOnTheHeadsAndNeverRead()
    {
        // Arrange: numbered up to the head.
        _tail.Followed.Publish(20);
        _tail.Numbering.Publish(20);
        var sut = Follower();

        // Act
        await RunOutAsync(sut.FollowAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken), TimeSpan.FromSeconds(1));

        // Assert
        _progress.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task Follow_WhenAReadFails_ShouldReadAgainAfterTheInterval()
    {
        // Arrange: the first read fails; the second finds the row.
        _tail.Followed.Publish(20);
        _progress.Holding(null, 20).FailingOnce(new InvalidOperationException("the database went away"));
        var sut = Follower();

        // Act: the following runs past one interval, so the second read happens.
        var following = sut.FollowAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var reached = _tail.WaitForNumberedAsync(20, TestContext.Current.CancellationToken).AsTask();
        while (!reached.IsCompleted)
        {
            _time.Advance(OrdinalSequencingFollower.Interval);
            await Task.WhenAny(reached, Task.Delay(10, TestContext.Current.CancellationToken));
        }

        await RunOutAsync(following, TimeSpan.FromSeconds(1));

        // Assert
        (await reached).ShouldBe(20);
        _progress.Reads.ShouldBe(2);
    }

    private OrdinalSequencingFollower Follower() =>
        new(new SharedSequence(_tail, _tail.Numbering), _progress, _time, NullLogger<OrdinalSequencingFollower>.Instance);

    /// <summary>Moves the clock past the time the following was given, until it returns.</summary>
    private async Task RunOutAsync(Task following, TimeSpan given)
    {
        _time.Advance(given);
        while (!following.IsCompleted)
        {
            await Task.WhenAny(following, Task.Delay(10, TestContext.Current.CancellationToken));
            _time.Advance(OrdinalSequencingFollower.Interval);
        }

        await following;
    }
}
