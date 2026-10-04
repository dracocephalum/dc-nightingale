using System.Data.Common;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The router with a scripted secondary and primary: every case asserts that the page is the one
/// the primary alone would have returned, and which of the two was asked for what.
/// </summary>
public sealed class ReplicaRouterTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<(long From, int Count)> _primaryAsked = [];
    private readonly ReplicaRouter _sut;
    private int _replicaAsked;

    public ReplicaRouterTests()
    {
        _sut = new ReplicaRouter(_time, NullLogger.Instance);
    }

    [Fact]
    public async Task ReadAsync_WhenTheSecondaryFillsThePage_ShouldNotAskThePrimary()
    {
        // Act
        var page = await ReadAsync(Direction.Forwards, from: 1, head: 100, count: 3, mark: 50, replica: [1, 2, 3]);

        // Assert
        page.ShouldBe([1, 2, 3]);
        _primaryAsked.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_WhenThePageIsShortAndTheMarkCoversTheHead_ShouldNotAskThePrimary()
    {
        // Act: the stream simply has no more events up to the head.
        var page = await ReadAsync(Direction.Forwards, from: 1, head: 40, count: 10, mark: 50, replica: [1, 2]);

        // Assert
        page.ShouldBe([1, 2]);
        _primaryAsked.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_WhenThePageReachesAboveTheMark_ShouldTakeTheRestFromThePrimary()
    {
        // Act: the secondary has positions up to 50; the page wants up to 100.
        var page = await ReadAsync(Direction.Forwards, from: 48, head: 100, count: 5, mark: 50, replica: [48, 50], primary: [51, 52, 53]);

        // Assert: the primary is asked from just above the mark, for what is left of the page.
        page.ShouldBe([48, 50, 51, 52, 53]);
        _primaryAsked.ShouldBe([(51, 3)]);
    }

    [Fact]
    public async Task ReadAsync_WhenThePageBeginsAboveTheMark_ShouldTakeAllOfItFromThePrimary()
    {
        // Act: the first look learns the mark; nothing of the page is at or below it.
        var page = await ReadAsync(Direction.Forwards, from: 60, head: 100, count: 2, mark: 50, replica: [], primary: [60, 61]);

        // Assert
        page.ShouldBe([60, 61]);
        _primaryAsked.ShouldBe([(60, 2)]);
    }

    [Fact]
    public async Task ReadAsync_WhenAPositionIsAboveAMarkSeenAMomentAgo_ShouldNotAskTheSecondaryAtAll()
    {
        // Arrange: a first read learns the mark.
        await ReadAsync(Direction.Forwards, from: 1, head: 100, count: 1, mark: 50, replica: [1]);
        _replicaAsked = 0;

        // Act: a reader at the head, then the same reader once the mark is no longer fresh.
        await ReadAsync(Direction.Forwards, from: 99, head: 100, count: 2, mark: 50, replica: [], primary: [99, 100]);
        var askedWhileFresh = _replicaAsked;
        _time.Advance(ReplicaRouter.MarkFreshness);
        await ReadAsync(Direction.Forwards, from: 99, head: 100, count: 2, mark: 100, replica: [99, 100]);

        // Assert
        askedWhileFresh.ShouldBe(0);
        _replicaAsked.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAsync_WhenBackwardsFromAtOrBelowTheMark_ShouldUseTheSecondary()
    {
        // Act
        var page = await ReadAsync(Direction.Backwards, from: 50, head: 100, count: 3, mark: 50, replica: [50, 49, 48]);

        // Assert
        page.ShouldBe([50, 49, 48]);
        _primaryAsked.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_WhenBackwardsFromAboveTheMark_ShouldUseThePrimary()
    {
        // Act: the secondary cannot vouch for what lies between its mark and the start.
        var page = await ReadAsync(Direction.Backwards, from: 60, head: 100, count: 3, mark: 50, replica: [], primary: [60, 59, 58]);

        // Assert
        page.ShouldBe([60, 59, 58]);
        _primaryAsked.ShouldBe([(60, 3)]);
    }

    [Fact]
    public async Task ReadAsync_WhenTheSecondaryFails_ShouldUseThePrimaryAndLeaveTheSecondaryAloneForAWhile()
    {
        // Act: the failure, a read during the backoff, and a read after it.
        var failed = await ReadAsync(Direction.Forwards, from: 1, head: 100, count: 2, mark: 50, replica: null, primary: [1, 2]);
        var during = await ReadAsync(Direction.Forwards, from: 1, head: 100, count: 2, mark: 50, replica: [1, 2], primary: [1, 2]);
        var askedDuring = _replicaAsked;
        _time.Advance(ReplicaRouter.FailureBackoff);
        var after = await ReadAsync(Direction.Forwards, from: 1, head: 100, count: 2, mark: 50, replica: [1, 2]);

        // Assert
        failed.ShouldBe([1, 2]);
        during.ShouldBe([1, 2]);
        askedDuring.ShouldBe(1);
        after.ShouldBe([1, 2]);
        _replicaAsked.ShouldBe(2);
        _primaryAsked.ShouldBe([(1, 2), (1, 2)]);
    }

    [Fact]
    public async Task PreferReplicaAsync_WhenTheSecondaryFails_ShouldAnswerFromThePrimary()
    {
        // Act
        var first = await _sut.PreferReplicaAsync(_ => Task.FromResult("secondary"), _ => Task.FromResult("primary"), TestContext.Current.CancellationToken);
        var failed = await _sut.PreferReplicaAsync<string>(_ => throw new ScriptedDbException(), _ => Task.FromResult("primary"), TestContext.Current.CancellationToken);
        var during = await _sut.PreferReplicaAsync(_ => Task.FromResult("secondary"), _ => Task.FromResult("primary"), TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBe("secondary");
        failed.ShouldBe("primary");
        during.ShouldBe("primary");
    }

    private static EventRecord At(long position) =>
        new(Guid.NewGuid(), "orders-1", position, position, "order_placed", DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty, []);

    /// <summary>Reads through the router; a <see langword="null"/> secondary script fails the secondary.</summary>
    private async Task<long[]> ReadAsync(Direction direction, long from, long head, int count, long mark, long[]? replica, long[]? primary = null)
    {
        var page = await _sut.ReadAsync(
            direction,
            from,
            head,
            count,
            _ =>
            {
                _replicaAsked++;
                return replica is null
                    ? throw new ScriptedDbException()
                    : Task.FromResult<(long, IReadOnlyList<EventRecord>)>((mark, replica.Select(At).ToList()));
            },
            (start, most, _) =>
            {
                _primaryAsked.Add((start, most));
                return Task.FromResult<IReadOnlyList<EventRecord>>((primary ?? []).Select(At).ToList());
            },
            TestContext.Current.CancellationToken);
        return page.Select(record => record.Position).ToArray();
    }

    private sealed class ScriptedDbException : DbException
    {
    }
}
