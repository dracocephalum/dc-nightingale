using System.Collections.Concurrent;

using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>
/// The tails over positions, a horizon and kept heads a test writes by hand: each sequence's head
/// follows its own committed prefix, a gap holds that sequence alone, a gap is given up to the
/// ceiling it was first seen under once the checkpoint over it is final, at once under a horizon
/// nothing holds, after the threshold under the best-effort one, and never while a transaction
/// holds the horizon; a look says whether a head is catching up, a refresh extends but never
/// skips, the heads begin where they were kept and are kept at every look, whoever follows every
/// sequence is woken by any of them, and the loop looks on its cadence. The two tenants'
/// positions overlap, as they do under a sequence per tenant.
/// </summary>
public sealed class SequenceTailsTests : IAsyncDisposable
{
    private static readonly SequenceKey A = new("a");
    private static readonly SequenceKey B = new("b");

    private readonly FakePositions _positions = new();
    private readonly FakeHorizon _horizon = new();
    private readonly FakeRows _rows = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly AppendSignal _appended;
    private HeadStrategy _strategy;
    private int _selections;
    private SequenceTails? _sut;

    public SequenceTailsTests()
    {
        _appended = new AppendSignal(_time);
        _strategy = new HeadStrategy(HeadStrategyKind.TransactionHorizon, _horizon, _positions);
    }

    private SequenceTails Sut => _sut ??= new SequenceTails(
        _ =>
        {
            Interlocked.Increment(ref _selections);
            return ValueTask.FromResult(_strategy);
        },
        _rows,
        _appended,
        _time,
        NullLogger<SequenceTails>.Instance);

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null)
        {
            await _sut.DisposeAsync();
        }
    }

    [Fact]
    public async Task Look_ShouldFollowEachSequencesOwnPrefixAndLetAGapHoldThatSequenceAlone()
    {
        // Arrange: tenant a has a gap at 4, held by an open transaction; tenant b has none, and its
        // positions are its own.
        _positions.Append(A, 1, 2, 3, 5);
        _positions.Append(B, 1, 2);
        _horizon.Hold();
        var a = Sut.For(A);
        var b = Sut.For(B);

        // Act
        var catchingUp = await Sut.LookAsync(TestContext.Current.CancellationToken);
        var again = await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert
        catchingUp.ShouldBeTrue("tenant a moved and was left short of 5");
        again.ShouldBeTrue("a gap waiting on its checkpoint is judged again on the fast interval");
        a.Head.ShouldBe(3);
        b.Head.ShouldBe(2);
        Sut.For(A).ShouldBeSameAs(a);
        Sut.Followed.ShouldBe([A, B], ignoreOrder: true);
        _positions.CeilingReads.ShouldBe(2, "one read over every followed sequence per look");
        _horizon.Marks.ShouldBe(1, "one checkpoint over the gap, judged at every look");
    }

    [Fact]
    public async Task Look_WithAGapInEachSequence_ShouldMarkOnceAndAskTheHorizonOnceForBoth()
    {
        // Arrange
        _positions.Append(A, 1, 3);
        _positions.Append(B, 1, 2, 4);
        _horizon.Hold();
        Sut.For(A);
        Sut.For(B);

        // Act
        await Sut.LookAsync(TestContext.Current.CancellationToken);
        await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert: the checkpoints are taken once, under one marker, and judged together at each look.
        _horizon.Marks.ShouldBe(1);
        _horizon.Asks.ShouldBe(2);
        Sut.For(A).Head.ShouldBe(1);
        Sut.For(B).Head.ShouldBe(2);
    }

    [Fact]
    public async Task Look_WhenNoSequenceIsFollowedOrNoneMoved_ShouldReadNothingMore()
    {
        // Arrange
        var none = await Sut.LookAsync(TestContext.Current.CancellationToken);
        _positions.Append(A, 1);
        Sut.For(A);
        await Sut.LookAsync(TestContext.Current.CancellationToken);
        var reads = _positions.FollowingReads;

        // Act
        var quiet = await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert: a sequence whose ceiling is its head is not read again.
        none.ShouldBeFalse();
        quiet.ShouldBeFalse();
        _positions.FollowingReads.ShouldBe(reads);
        _horizon.Marks.ShouldBe(0, "a store with no gap never asks the horizon");
    }

    [Fact]
    public async Task Look_WhenTheHorizonCannotBeAsked_ShouldStillMoveTheHeadUpToTheGap()
    {
        // Arrange: committed through 3, 4 missing, and a horizon that fails when asked.
        _positions.Append(A, 1, 2, 3, 5);
        _horizon.Fail();
        var a = Sut.For(A);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => Sut.LookAsync(TestContext.Current.CancellationToken));

        // Assert: the gap waits on the horizon; the committed run before it does not.
        a.Head.ShouldBe(3);
    }

    [Fact]
    public async Task Look_WhenNothingHoldsTheHorizon_ShouldGiveTheGapUpAtOnceAndMoveToTheCeiling()
    {
        // Arrange: 4 was taken by a transaction that rolled back; nothing is open.
        _positions.Append(A, 1, 2, 3, 5, 6);
        var a = Sut.For(A);

        // Act
        var catchingUp = await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert: given up in the look that first saw it, no clock involved.
        catchingUp.ShouldBeFalse();
        a.Head.ShouldBe(6);
    }

    [Fact]
    public async Task Look_WhileATransactionHoldsTheHorizon_ShouldHoldTheHeadAndThenGiveUpOnlyWhatWasSeenWhenTheGapWasFirstSeen()
    {
        // Arrange: 4 is open when the gap is first seen under the ceiling 5.
        _positions.Append(A, 1, 2, 3, 5);
        _horizon.Hold();
        var a = Sut.For(A);
        await Sut.LookAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(5));
        await Sut.LookAsync(TestContext.Current.CancellationToken);
        var held = a.Head;

        // Act: later appends commit, 8 among them taken and not yet committed; the transaction
        // holding 4 rolls back.
        _positions.Append(A, 6, 7, 9);
        _horizon.Release();
        await Sut.LookAsync(TestContext.Current.CancellationToken);
        var released = a.Head;
        await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert: no wait while it was held, however long; then the span up to 5, and on through
        // 6 and 7, which follow it; 8 has a checkpoint of its own, taken and judged the next look.
        held.ShouldBe(3);
        released.ShouldBe(7);
        a.Head.ShouldBe(9);
    }

    [Fact]
    public async Task Look_UnderTheBestEffortStrategy_ShouldGiveTheGapUpOnceTheCheckpointIsAsOldAsTheThreshold()
    {
        // Arrange
        _strategy = new HeadStrategy(HeadStrategyKind.BestEffort, new StaleThresholdHorizon(StaleThresholdHorizon.DefaultThreshold), _positions);
        _positions.Append(A, 1, 2, 3, 5);
        var a = Sut.For(A);

        // Act: the gap is seen under the ceiling 5, then 6 is taken, 7 committed.
        await Sut.LookAsync(TestContext.Current.CancellationToken);
        _positions.Append(A, 7);
        _time.Advance(StaleThresholdHorizon.DefaultThreshold - TimeSpan.FromMilliseconds(1));
        await Sut.LookAsync(TestContext.Current.CancellationToken);
        var early = a.Head;
        _time.Advance(TimeSpan.FromMilliseconds(1));
        var catchingUp = await Sut.LookAsync(TestContext.Current.CancellationToken);
        var given = a.Head;
        await Sut.LookAsync(TestContext.Current.CancellationToken);
        _time.Advance(StaleThresholdHorizon.DefaultThreshold);
        await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert: up to the first checkpoint's ceiling, then 6 waits out a threshold of its own.
        early.ShouldBe(3);
        given.ShouldBe(5);
        catchingUp.ShouldBeTrue("7 is seen past the head");
        a.Head.ShouldBe(7);
    }

    [Fact]
    public async Task Look_ShouldCallForTheFastIntervalOnlyWhileAHeadItMovedIsStillBehind()
    {
        // Arrange: tenant a's positions follow one another to the last; tenant b has more than
        // one window of them.
        _positions.Append(A, 1, 2, 3);
        _positions.Append(B, Enumerable.Range(1, SequenceTails.RefreshWindow + 1).Select(position => (long)position).ToArray());
        var a = Sut.For(A);

        // Act
        var caughtUp = await Sut.LookAsync(TestContext.Current.CancellationToken);
        var b = Sut.For(B);
        var windowFull = await Sut.LookAsync(TestContext.Current.CancellationToken);
        var rest = await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert
        caughtUp.ShouldBeFalse("a moved to its last position");
        a.Head.ShouldBe(3);
        windowFull.ShouldBeTrue("b moved one window and has more past it");
        rest.ShouldBeFalse("the second window reached b's last position");
        b.Head.ShouldBe(SequenceTails.RefreshWindow + 1);
    }

    [Fact]
    public async Task StopNumbering_ShouldStopEveryTailsNumberingNowAndLaterAndLeaveTheHeadsMoving()
    {
        // Arrange: one tail followed before the switch, one after; the operator's reason.
        _positions.Append(A, 1, 2);
        var before = Sut.For(A);
        var reason = new OrdinalSequencerDisabledException();

        // Act
        var waiting = before.WaitForNumberedAsync(1, TestContext.Current.CancellationToken).AsTask();
        Sut.StopNumbering(reason);
        var after = Sut.For(B);
        await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert: a wait under way ends with the reason, a wait begun after it too; the head of
        // the sequence still moves, since only the numbering is stopped.
        (await Should.ThrowAsync<OrdinalSequencerDisabledException>(() => waiting)).ShouldBeSameAs(reason);
        before.NumberingStopped.ShouldBeSameAs(reason);
        after.NumberingStopped.ShouldBeSameAs(reason);
        await Should.ThrowAsync<OrdinalSequencerDisabledException>(() => after.WaitForNumberedAsync(1, TestContext.Current.CancellationToken).AsTask());
        before.Head.ShouldBe(2);
    }

    [Fact]
    public async Task Refresh_ShouldExtendThroughWhatIsCommittedAndStopAtAGapWhateverTheHorizonSays()
    {
        // Arrange: nothing holds the horizon; a look would give 3 up.
        _positions.Append(A, 1, 2, 4);
        var a = Sut.For(A);

        // Act
        var first = await a.RefreshAsync(TestContext.Current.CancellationToken);
        _positions.Append(A, 3);
        var closed = await a.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBe(2, "a refresh never gives a gap up; the poller does");
        closed.ShouldBe(4);
        _horizon.Marks.ShouldBe(0);
    }

    [Fact]
    public async Task Look_ShouldBeginEachSequenceAtTheHeadKeptForItAndKeepEveryHeadItLooksAt()
    {
        // Arrange: a head kept by an earlier process at 10; the events up to it are not read again.
        _rows.Kept[A] = 10;
        _positions.Append(A, 4, 11, 12);
        _positions.Append(B, 1);
        var a = Sut.For(A);
        Sut.For(B);

        // Act
        await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert
        a.Head.ShouldBe(12);
        _rows.Kept[A].ShouldBe(12);
        _rows.Kept[B].ShouldBe(1);
        _rows.CheckedAt.ShouldBe(_time.GetUtcNow());
        _positions.FollowedFrom(A).ShouldBe([10L]);
    }

    [Fact]
    public async Task Look_WhenKeepingTheHeadsFails_ShouldStillMoveThem()
    {
        // Arrange
        _positions.Append(A, 1, 2);
        _rows.FailWrites = true;
        var a = Sut.For(A);

        // Act
        var catchingUp = await Sut.LookAsync(TestContext.Current.CancellationToken);

        // Assert
        catchingUp.ShouldBeFalse();
        a.Head.ShouldBe(2);
    }

    [Fact]
    public async Task Strategy_ShouldBeChosenOnceForTheLifeOfTheTails()
    {
        // Arrange
        _positions.Append(A, 1);
        var a = Sut.For(A);

        // Act: a refresh before the start chooses it; the start and its looks keep it.
        await a.RefreshAsync(TestContext.Current.CancellationToken);
        await Sut.StartAsync(TestContext.Current.CancellationToken);
        while (_positions.CeilingReads == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await Sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        _selections.ShouldBe(1);
        Sut.Strategy.ShouldBeSameAs(_strategy);
    }

    [Fact]
    public async Task WaitForAnyAdvance_ShouldEndWhenAnySequencesHeadMovesAndCountTheMoves()
    {
        // Arrange
        var a = Sut.For(A);
        Sut.For(B);
        var seen = Sut.Advances;
        var waiting = Sut.WaitForAnyAdvanceAsync(seen, TestContext.Current.CancellationToken);

        // Act
        _positions.Append(B, 1);
        await Sut.LookAsync(TestContext.Current.CancellationToken);
        var advances = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        seen.ShouldBe(0);
        advances.ShouldBe(1);
        a.Head.ShouldBe(0, "the other tenant's events are not this one's");
        (await Sut.WaitForAnyAdvanceAsync(0, TestContext.Current.CancellationToken)).ShouldBe(1, "a count already passed answers at once");
    }

    [Fact]
    public async Task Poll_ShouldLookOnTheCadenceUntilStoppedAndGoOnAfterALookFails()
    {
        // Arrange: the first look fails; the loop waits out the interval and looks again.
        var a = Sut.For(A);
        _positions.Append(A, 1);
        _positions.FailNextRead();

        // Act
        await Sut.StartAsync(TestContext.Current.CancellationToken);
        var waiting = a.WaitForAdvanceAsync(0, TestContext.Current.CancellationToken).AsTask();
        while (!waiting.IsCompleted)
        {
            _time.Advance(SequenceTails.SlowInterval);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await Sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        (await waiting).ShouldBe(1);
        _positions.CeilingReads.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Poll_WhenAnAppendIsSignalled_ShouldLookAtOnceInsteadOfWaitingOutTheInterval()
    {
        // Arrange: the loop has looked once at a sequence with nothing in it and is waiting out
        // the slow interval, on a clock that never moves.
        var a = Sut.For(A);
        await Sut.StartAsync(TestContext.Current.CancellationToken);
        while (_positions.CeilingReads == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // Act: an append commits and says so.
        _positions.Append(A, 1);
        _appended.Pulse();
        var head = await a.WaitForAdvanceAsync(0, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        head.ShouldBe(1);
    }

    /// <summary>The positions, by sequence; what the tails read, written by the test.</summary>
    private sealed class FakePositions : ISequencePositions
    {
        private readonly ConcurrentDictionary<SequenceKey, SortedSet<long>> _rows = new();
        private readonly ConcurrentQueue<(SequenceKey Sequence, long After)> _following = new();
        private int _failNext;
        private int _ceilingReads;

        public int CeilingReads => Volatile.Read(ref _ceilingReads);

        public int FollowingReads => _following.Count;

        public void Append(SequenceKey sequence, params long[] positions)
        {
            var rows = _rows.GetOrAdd(sequence, _ => []);
            lock (rows)
            {
                rows.UnionWith(positions);
            }
        }

        public void FailNextRead() => _failNext = 1;

        public List<long> FollowedFrom(SequenceKey sequence) => _following.Where(read => read.Sequence == sequence).Select(read => read.After).ToList();

        public Task<IReadOnlyDictionary<SequenceKey, long>> ReadCeilingsAsync(IReadOnlyCollection<SequenceKey> sequences, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _ceilingReads);
            if (Interlocked.Exchange(ref _failNext, 0) == 1)
            {
                throw new InvalidOperationException("The database went away.");
            }

            var ceilings = new Dictionary<SequenceKey, long>();
            foreach (var sequence in sequences)
            {
                var rows = Snapshot(sequence);
                if (rows.Count > 0)
                {
                    ceilings[sequence] = rows[^1];
                }
            }

            return Task.FromResult<IReadOnlyDictionary<SequenceKey, long>>(ceilings);
        }

        public Task<IReadOnlyList<long>> ReadFollowingAsync(SequenceKey sequence, long after, int window, CancellationToken cancellationToken)
        {
            _following.Enqueue((sequence, after));
            return Task.FromResult<IReadOnlyList<long>>(Snapshot(sequence).Where(position => position > after).Take(window).ToList());
        }

        private List<long> Snapshot(SequenceKey sequence)
        {
            if (!_rows.TryGetValue(sequence, out var rows))
            {
                return [];
            }

            lock (rows)
            {
                return rows.ToList();
            }
        }
    }

    /// <summary>A horizon a test holds and releases: an open transaction, or none.</summary>
    private sealed class FakeHorizon : ITransactionHorizon
    {
        private HorizonHolder? _holder;
        private Exception? _failure;
        private int _marks;
        private int _asks;

        public int Marks => Volatile.Read(ref _marks);

        public int Asks => Volatile.Read(ref _asks);

        public TimeSpan MinimumAge => TimeSpan.Zero;

        public void Hold() => _holder = new HorizonHolder(57, "appender", null);

        public void Release() => _holder = null;

        public void Fail() => _failure = new InvalidOperationException("The horizon cannot be read.");

        public ValueTask<HorizonAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(HorizonAvailability.Available);

        public ValueTask<HorizonMarker> MarkAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new HorizonMarker(Interlocked.Increment(ref _marks)));

        public ValueTask<IReadOnlyDictionary<SequenceKey, HorizonHolder>> FindHoldersAsync(IReadOnlyDictionary<SequenceKey, HorizonMarker> markers, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _asks);
            return _failure is { } failure
                ? ValueTask.FromException<IReadOnlyDictionary<SequenceKey, HorizonHolder>>(failure)
                : ValueTask.FromResult<IReadOnlyDictionary<SequenceKey, HorizonHolder>>(_holder is { } holder ? markers.Keys.ToDictionary(key => key, _ => holder) : []);
        }
    }

    /// <summary>The kept heads, in memory: raised, never lowered, as the table keeps them.</summary>
    private sealed class FakeRows : ISequenceHeadRows
    {
        public ConcurrentDictionary<SequenceKey, long> Kept { get; } = new();

        public DateTimeOffset? CheckedAt { get; private set; }

        public bool FailWrites { get; set; }

        public Task<IReadOnlyDictionary<SequenceKey, long>> ReadAsync(IReadOnlyCollection<SequenceKey> sequences, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<SequenceKey, long>>(sequences.Where(Kept.ContainsKey).ToDictionary(sequence => sequence, sequence => Kept[sequence]));

        public Task WriteAsync(IReadOnlyDictionary<SequenceKey, long> heads, DateTimeOffset checkedAt, CancellationToken cancellationToken)
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("The table went away.");
            }

            foreach (var (sequence, head) in heads)
            {
                Kept.AddOrUpdate(sequence, head, (_, kept) => Math.Max(kept, head));
            }

            CheckedAt = checkedAt;
            return Task.CompletedTask;
        }
    }
}
