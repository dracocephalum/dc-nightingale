using System.Collections.Concurrent;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The per-tenant poller over positions a test writes by hand: each tenant's head follows its own
/// committed prefix, a gap in one tenant holds that tenant alone, a gap held past the threshold
/// is given up the store's way, a refresh extends but never skips, whoever follows every tenant
/// is woken by any of them, and the loop looks on its cadence. The two tenants' positions
/// overlap, as they do under a sequence per tenant. The queries themselves are
/// <c>EventRowPositionsTests</c>'.
/// </summary>
public sealed class PolecatTenantTailsTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly FakePositions _positions = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly AppendSignal _appended;
    private readonly PolecatTenantTails _sut;

    public PolecatTenantTailsTests()
    {
        _appended = new AppendSignal(_time);
        _sut = new PolecatTenantTails(_positions, _appended, _time, NullLogger<PolecatTenantTails>.Instance);
    }

    public ValueTask DisposeAsync() => _sut.DisposeAsync();

    [Fact]
    public async Task Look_ShouldFollowEachTenantsOwnPrefixAndLetAGapHoldThatTenantAlone()
    {
        // Arrange: tenant a has a gap at 4; tenant b has none, and its positions are its own.
        _positions.Append("a", 1, 2, 3, 5);
        _positions.Append("b", 1, 2);
        var a = _sut.For("a");
        var b = _sut.For("b");

        // Act
        var moved = await _sut.LookAsync(TestContext.Current.CancellationToken);
        var again = await _sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert
        moved.ShouldBeTrue();
        again.ShouldBeFalse("nothing was appended between the looks");
        a.Head.ShouldBe(3);
        b.Head.ShouldBe(2);
        _sut.For("a").ShouldBeSameAs(a);
        _sut.PolledTenants.ShouldBe(["a", "b"], ignoreOrder: true);
        _positions.CeilingReads.ShouldBe(2, "one read over every polled tenant per look");
    }

    [Fact]
    public async Task Look_WhenNoTenantIsPolledOrNoneMoved_ShouldReadNothingMore()
    {
        // Arrange
        var none = await _sut.LookAsync(TestContext.Current.CancellationToken);
        _positions.Append("a", 1);
        _sut.For("a");
        await _sut.LookAsync(TestContext.Current.CancellationToken);
        var reads = _positions.FollowingReads;

        // Act
        var quiet = await _sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert: a tenant whose ceiling is its head is not read again.
        none.ShouldBeFalse();
        quiet.ShouldBeFalse();
        _positions.FollowingReads.ShouldBe(reads);
    }

    [Fact]
    public async Task Look_WhenAGapHoldsTheHeadPastTheThreshold_ShouldGiveItUpToTheLastPositionAppendedBeforeTheThreshold()
    {
        // Arrange: 4 is taken by a transaction that never commits; 5 and 6 were appended long
        // ago, 7 within the threshold.
        _positions.Append("a", 1, 2, 3, 5, 6);
        _positions.Append("a", Now.AddMilliseconds(1), 7);
        var a = _sut.For("a");

        // Act: the gap is seen by the look that reaches 3, then holds the head for the threshold.
        await _sut.LookAsync(TestContext.Current.CancellationToken);
        var held = a.Head;
        _time.Advance(PolecatTenantTails.StaleAfter - TimeSpan.FromMilliseconds(1));
        await _sut.LookAsync(TestContext.Current.CancellationToken);
        var stillHeld = a.Head;
        _time.Advance(TimeSpan.FromMilliseconds(1));
        var moved = await _sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert: the head moves to 6, the last position appended before the threshold, and
        // on through 7, which follows it without a gap.
        held.ShouldBe(3);
        stillHeld.ShouldBe(3);
        moved.ShouldBeTrue();
        a.Head.ShouldBe(7);
    }

    [Fact]
    public async Task Look_WhenEverythingPastTheGapIsWithinTheThreshold_ShouldKeepHolding()
    {
        // Arrange: the gap is old enough to give up, but what follows it was appended a moment ago.
        _positions.Append("a", 1, 2);
        _positions.Append("a", Now.AddSeconds(2), 4);
        var a = _sut.For("a");

        // Act
        await _sut.LookAsync(TestContext.Current.CancellationToken);
        _time.Advance(PolecatTenantTails.StaleAfter);
        var moved = await _sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert
        moved.ShouldBeFalse();
        a.Head.ShouldBe(2);
    }

    [Fact]
    public async Task Refresh_ShouldExtendThroughWhatIsCommittedAndStopAtAGapWhateverItsAge()
    {
        // Arrange
        _positions.Append("a", 1, 2, 4);
        var a = _sut.For("a");

        // Act
        var first = await a.RefreshAsync(TestContext.Current.CancellationToken);
        _time.Advance(PolecatTenantTails.StaleAfter * 2);
        var later = await a.RefreshAsync(TestContext.Current.CancellationToken);
        _positions.Append("a", 3);
        var closed = await a.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBe(2);
        later.ShouldBe(2, "a refresh never gives a gap up; the poller does");
        closed.ShouldBe(4);
    }

    [Fact]
    public async Task WaitForAnyAdvance_ShouldEndWhenAnyTenantsHeadMovesAndCountTheMoves()
    {
        // Arrange
        var a = _sut.For("a");
        _sut.For("b");
        var seen = _sut.Advances;
        var waiting = _sut.WaitForAnyAdvanceAsync(seen, TestContext.Current.CancellationToken);

        // Act
        _positions.Append("b", 1);
        await _sut.LookAsync(TestContext.Current.CancellationToken);
        var advances = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        seen.ShouldBe(0);
        advances.ShouldBe(1);
        a.Head.ShouldBe(0, "the other tenant's events are not this one's");
        (await _sut.WaitForAnyAdvanceAsync(0, TestContext.Current.CancellationToken)).ShouldBe(1, "a count already passed answers at once");
    }

    [Fact]
    public async Task Poll_ShouldLookOnTheCadenceUntilStoppedAndGoOnAfterALookFails()
    {
        // Arrange: the first look fails; the loop waits out the interval and looks again.
        var a = _sut.For("a");
        _positions.Append("a", 1);
        _positions.FailNextRead();

        // Act
        await _sut.StartAsync(TestContext.Current.CancellationToken);
        var waiting = a.WaitForAdvanceAsync(0, TestContext.Current.CancellationToken).AsTask();
        while (!waiting.IsCompleted)
        {
            _time.Advance(PolecatTenantTails.SlowInterval);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await _sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        (await waiting).ShouldBe(1);
        _positions.CeilingReads.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Poll_WhenAnAppendIsSignalled_ShouldLookAtOnceInsteadOfWaitingOutTheInterval()
    {
        // Arrange: the loop has looked once at a tenant with nothing in it and is waiting out
        // the slow interval, on a clock that never moves.
        var a = _sut.For("a");
        await _sut.StartAsync(TestContext.Current.CancellationToken);
        while (_positions.CeilingReads == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // Act: an append commits and says so.
        _positions.Append("a", 1);
        _appended.Pulse();
        var head = await a.WaitForAdvanceAsync(0, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await _sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        head.ShouldBe(1);
    }

    /// <summary>The positions, by tenant, with when each was appended; what the poller reads, written by the test.</summary>
    private sealed class FakePositions : ITenantEventPositions
    {
        private readonly ConcurrentDictionary<string, SortedDictionary<long, DateTimeOffset>> _rows = new(StringComparer.Ordinal);
        private int _failNext;

        public int CeilingReads { get; private set; }

        public int FollowingReads { get; private set; }

        public void Append(string tenant, params long[] positions) => Append(tenant, Now - TimeSpan.FromMinutes(10), positions);

        public void Append(string tenant, DateTimeOffset appendedAt, params long[] positions)
        {
            var rows = _rows.GetOrAdd(tenant, _ => []);
            lock (rows)
            {
                foreach (var position in positions)
                {
                    rows[position] = appendedAt;
                }
            }
        }

        public void FailNextRead() => _failNext = 1;

        public Task<IReadOnlyDictionary<string, long>> ReadCeilingsAsync(IReadOnlyCollection<string> tenantIds, CancellationToken cancellationToken)
        {
            CeilingReads++;
            if (Interlocked.Exchange(ref _failNext, 0) == 1)
            {
                throw new InvalidOperationException("The database went away.");
            }

            var ceilings = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var tenant in tenantIds)
            {
                if (_rows.TryGetValue(tenant, out var rows))
                {
                    lock (rows)
                    {
                        if (rows.Count > 0)
                        {
                            ceilings[tenant] = rows.Keys.Max();
                        }
                    }
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, long>>(ceilings);
        }

        public Task<IReadOnlyList<long>> ReadFollowingAsync(string tenantId, long mark, int window, CancellationToken cancellationToken)
        {
            FollowingReads++;
            return Task.FromResult<IReadOnlyList<long>>(Snapshot(tenantId).Where(row => row.Key > mark).Select(row => row.Key).Take(window).ToList());
        }

        public Task<long?> FindLastAppendedBeforeAsync(string tenantId, long mark, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot(tenantId).Where(row => row.Key > mark && row.Value <= cutoff).Select(row => (long?)row.Key).Max());

        private List<KeyValuePair<long, DateTimeOffset>> Snapshot(string tenant)
        {
            if (!_rows.TryGetValue(tenant, out var rows))
            {
                return [];
            }

            lock (rows)
            {
                return rows.ToList();
            }
        }
    }
}
