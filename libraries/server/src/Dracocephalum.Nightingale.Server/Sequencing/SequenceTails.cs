using System.Collections.Concurrent;

using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The tails of a journal: a sequence head per sequence, the shared one or each tenant's, kept by
/// one poller for the process, every backend's. A head is the last position up to which every
/// event of its sequence is final, found by extending it through the positions that follow it one
/// by one. A gap, a position taken by a transaction not yet committed or one that never will be,
/// is judged by a <see cref="HorizonCheckpoint"/> over the highest position seen when it was first
/// seen; once the checkpoint is final the head moves to its ceiling at once, the whole span with
/// it, and extends from there. The strategy that judges is chosen once, when the tails start: the
/// database's transaction horizon where it answers, the stale threshold where it does not, and it
/// holds for the life of the process. One round trip per look finds which sequences moved, and
/// only those are read; the interval is the fast one while a look moved a head and left it short
/// of positions already seen past it, a head catching up, or while a gap waits on its checkpoint,
/// and the slow one once every head has caught up. An append
/// made through this process cuts either short: the poller waits on the append signal. A sequence
/// is followed from the first time its tail is asked for, for the life of the process, beginning
/// at the head kept for it in the gateway's own table, which every look raises and stamps. A look
/// that fails is logged and taken again at the next interval: the heads stay where they were, and
/// whoever waits on them waits on.
/// </summary>
public sealed partial class SequenceTails : IHostedService, IAsyncDisposable
{
    /// <summary>How long the poller waits after a look that left a head behind positions seen past it, catching up or held by a gap: the store's fast polling time.</summary>
    public static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the poller waits after a look that found every head caught up: the store's slow polling time.</summary>
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a gap is held by an open transaction before the transaction is named in a warning.</summary>
    public static readonly TimeSpan HoldWarning = TimeSpan.FromSeconds(30);

    /// <summary>How many positions past a head one extension reads.</summary>
    public const int RefreshWindow = 10_000;

    private readonly Func<CancellationToken, ValueTask<HeadStrategy>> _selectStrategy;
    private readonly ISequenceHeadRows _rows;
    private readonly AppendSignal _appended;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SequenceTails> _logger;
    private readonly TimeSpan _fast;
    private readonly TimeSpan _slow;
    private readonly ConcurrentDictionary<SequenceKey, Tail> _tails = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _choosing = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource _anyAdvanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HeadStrategy? _strategy;
    private OrdinalSequencerException? _numberingStopped;
    private long _advances;
    private Task? _loop;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="SequenceTails"/> class.</summary>
    /// <param name="selectStrategy">Chooses the strategy, once, when the tails start or are first read.</param>
    /// <param name="contexts">Makes the gateway's context, where the heads are kept.</param>
    /// <param name="appended">Pulsed after each append this process commits; a wait between looks ends at a pulse.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="fastInterval">How long the poller waits after a look that moved a head and left it behind; <see cref="FastInterval"/> when not given.</param>
    /// <param name="slowInterval">How long it waits after any other look; <see cref="SlowInterval"/> when not given.</param>
    public SequenceTails(
        Func<CancellationToken, ValueTask<HeadStrategy>> selectStrategy,
        IDbContextFactory<NightingaleDbContext> contexts,
        AppendSignal appended,
        TimeProvider timeProvider,
        ILogger<SequenceTails> logger,
        TimeSpan? fastInterval = null,
        TimeSpan? slowInterval = null)
        : this(selectStrategy, new SequenceHeadRows(contexts), appended, timeProvider, logger, fastInterval, slowInterval)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SequenceTails"/> class over heads kept wherever a test keeps them.</summary>
    /// <param name="selectStrategy">Chooses the strategy.</param>
    /// <param name="rows">Where the heads are kept.</param>
    /// <param name="appended">The append signal.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="fastInterval">The fast interval.</param>
    /// <param name="slowInterval">The slow interval.</param>
    internal SequenceTails(
        Func<CancellationToken, ValueTask<HeadStrategy>> selectStrategy,
        ISequenceHeadRows rows,
        AppendSignal appended,
        TimeProvider timeProvider,
        ILogger<SequenceTails> logger,
        TimeSpan? fastInterval = null,
        TimeSpan? slowInterval = null)
    {
        ArgumentNullException.ThrowIfNull(selectStrategy);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(appended);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _selectStrategy = selectStrategy;
        _rows = rows;
        _appended = appended;
        _timeProvider = timeProvider;
        _logger = logger;
        _fast = fastInterval ?? FastInterval;
        _slow = slowInterval ?? SlowInterval;
    }

    /// <summary>Gets the strategy the heads are kept by, once chosen; <see langword="null"/> before the tails start.</summary>
    public HeadStrategy? Strategy => Volatile.Read(ref _strategy);

    /// <summary>Gets how many times any sequence's head has moved; what <see cref="WaitForAnyAdvanceAsync"/> is asked against.</summary>
    public long Advances
    {
        get
        {
            lock (_gate)
            {
                return _advances;
            }
        }
    }

    /// <summary>Gets the sequences followed: those whose tail has been asked for.</summary>
    public IReadOnlyCollection<SequenceKey> Followed => _tails.Keys.ToArray();

    /// <summary>The tail of a sequence, followed from now on.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <returns>The tail.</returns>
    public IStoreTail For(SequenceKey sequence) => _tails.GetOrAdd(sequence, CreateTail);

    /// <summary>
    /// Stops the numbering of every sequence, followed now or later, for the life of the process:
    /// what the operator's switch for the ordinal sequencer does, so that every wait on the
    /// numbering ends with the reason and every read by ordinal is refused with it.
    /// </summary>
    /// <param name="reason">What every waiter and reader is told.</param>
    public void StopNumbering(OrdinalSequencerException reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Volatile.Write(ref _numberingStopped, reason);
        foreach (var tail in _tails.Values)
        {
            tail.Numbering.Stop(reason);
        }
    }

    /// <summary>The numbering of a sequence as it is followed: where the sequencer, or whoever follows it for this process, publishes how far it has come.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <returns>The followed numbering.</returns>
    public FollowedHead NumberingOf(SequenceKey sequence) => ((Tail)For(sequence)).Numbering;

    /// <summary>Waits until any sequence's head has moved since a count of advances was read; for whoever follows every sequence, the sequencer.</summary>
    /// <param name="seen">The count of advances the caller has already acted on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count of advances, greater than <paramref name="seen"/>.</returns>
    public async Task<long> WaitForAnyAdvanceAsync(long seen, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task advanced;
            lock (_gate)
            {
                if (_advances > seen)
                {
                    return _advances;
                }

                advanced = _anyAdvanced.Task;
            }

            await advanced.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var strategy = await ChooseStrategyAsync(cancellationToken).ConfigureAwait(false);
        LogStrategy(strategy.Kind);
        _loop = PollAsync(_stopping.Token);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loop is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _loop = null;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _stopping.Dispose();
        _choosing.Dispose();
    }

    /// <summary>
    /// One look at every followed sequence: which moved past its head, by one read over all of
    /// them; each of those extended; the gaps left judged together, under one marker for those
    /// first seen and one question to the horizon for all; then every head kept. The poller looks
    /// on its cadence; a test looks by hand.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether a head moved and was left behind positions already seen past it: a head catching up, which calls for the next look soon.</returns>
    internal async Task<bool> LookAsync(CancellationToken cancellationToken)
    {
        var tails = _tails.Values.ToArray();
        if (tails.Length == 0)
        {
            return false;
        }

        var strategy = await ChooseStrategyAsync(cancellationToken).ConfigureAwait(false);
        await BeginAsync(tails, cancellationToken).ConfigureAwait(false);
        var ceilings = await strategy.Positions.ReadCeilingsAsync(tails.Select(tail => tail.Key).ToArray(), cancellationToken).ConfigureAwait(false);
        var catchingUp = false;
        var gapped = new List<Tail>();
        foreach (var tail in tails)
        {
            if (ceilings.TryGetValue(tail.Key, out var ceiling) && ceiling > tail.Head)
            {
                var extension = await ExtendAsync(tail, ceiling, strategy, cancellationToken).ConfigureAwait(false);
                catchingUp |= extension == Extension.FullWindow;
                if (extension == Extension.Gapped)
                {
                    gapped.Add(tail);
                }
            }
            else
            {
                // Nothing past the head: whatever gap held it has filled or was given up.
                tail.Checkpoint = null;
            }
        }

        if (gapped.Count > 0)
        {
            catchingUp |= await JudgeAsync(gapped, ceilings, strategy, cancellationToken).ConfigureAwait(false);
        }

        await KeepAsync(tails, cancellationToken).ConfigureAwait(false);
        return catchingUp;
    }

    private async ValueTask<HeadStrategy> ChooseStrategyAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _strategy) is { } chosen)
        {
            return chosen;
        }

        await _choosing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_strategy is null)
            {
                Volatile.Write(ref _strategy, await _selectStrategy(cancellationToken).ConfigureAwait(false));
            }

            return _strategy!;
        }
        finally
        {
            _choosing.Release();
        }
    }

    /// <summary>Begins the tails not yet begun at the heads kept for them, so a process that starts does not walk the table from the start.</summary>
    private async Task BeginAsync(IReadOnlyCollection<Tail> tails, CancellationToken cancellationToken)
    {
        var unbegun = tails.Where(tail => !tail.Begun).ToArray();
        if (unbegun.Length == 0)
        {
            return;
        }

        var kept = await _rows.ReadAsync(unbegun.Select(tail => tail.Key).ToArray(), cancellationToken).ConfigureAwait(false);
        foreach (var tail in unbegun)
        {
            if (kept.TryGetValue(tail.Key, out var head))
            {
                Publish(tail, head);
            }

            tail.Begun = true;
        }
    }

    /// <summary>Raises every head kept and stamps when it was checked; a failure costs the replicas and the next start a head that lags, never a look.</summary>
    private async Task KeepAsync(IReadOnlyCollection<Tail> tails, CancellationToken cancellationToken)
    {
        try
        {
            await _rows.WriteAsync(tails.ToDictionary(tail => tail.Key, tail => tail.Head), _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogKeepFailed(exception);
        }
    }

    private async Task PollAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var catchingUp = false;
            try
            {
                catchingUp = await LookAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogLookFailed(exception);
            }

            try
            {
                await _appended.WaitAsync(catchingUp ? _fast : _slow, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Brings a sequence's head up to date now, through the positions that follow it without a gap; a gap is left to the poller.</summary>
    private async ValueTask<long> RefreshAsync(Tail tail, CancellationToken cancellationToken)
    {
        var strategy = await ChooseStrategyAsync(cancellationToken).ConfigureAwait(false);
        await BeginAsync([tail], cancellationToken).ConfigureAwait(false);
        var head = tail.Head;
        var following = await strategy.Positions.ReadFollowingAsync(tail.Key, head, RefreshWindow, cancellationToken).ConfigureAwait(false);
        Publish(tail, ContiguousPrefix.Extend(head, following));
        return tail.Head;
    }

    /// <summary>Extends a sequence's head through the positions that follow it one by one, up to the first gap.</summary>
    /// <param name="tail">The tail.</param>
    /// <param name="ceiling">The highest position of the sequence seen at the start of this look, before any marker is taken.</param>
    /// <param name="strategy">The strategy.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How far the head came: to the ceiling, through a full window, or to a gap left to judge.</returns>
    private async Task<Extension> ExtendAsync(Tail tail, long ceiling, HeadStrategy strategy, CancellationToken cancellationToken)
    {
        var following = await strategy.Positions.ReadFollowingAsync(tail.Key, tail.Head, RefreshWindow, cancellationToken).ConfigureAwait(false);
        var reached = ContiguousPrefix.Extend(tail.Head, following);

        // What was reached needs no judgement, so it is published first: a horizon that cannot be
        // asked holds a gap, never the run of committed positions before it.
        Publish(tail, reached);
        if (following.Count == RefreshWindow && reached == following[^1])
        {
            // A full window without a gap: a head catching up, with no gap to judge yet.
            tail.Checkpoint = null;
            return Extension.FullWindow;
        }

        if (reached >= ceiling)
        {
            tail.Checkpoint = null;
            return Extension.CaughtUp;
        }

        return Extension.Gapped;
    }

    /// <summary>
    /// Judges the gaps a look left, and moves past those that are final. A checkpoint over a gap
    /// is taken the first time it is seen, at the ceiling read before its marker, one marker for
    /// every gap first seen in the look, and judged at every look: at once, where the horizon
    /// needs no age.
    /// </summary>
    /// <returns>Whether a head is left behind positions seen past it: a gap still held, or another past the ceiling.</returns>
    private async Task<bool> JudgeAsync(IReadOnlyList<Tail> gapped, IReadOnlyDictionary<SequenceKey, long> ceilings, HeadStrategy strategy, CancellationToken cancellationToken)
    {
        var unmarked = gapped.Where(tail => tail.Checkpoint is null).ToDictionary(tail => tail.Key, tail => ceilings[tail.Key]);
        if (unmarked.Count > 0)
        {
            var taken = await HorizonCheckpoint.TakeAsync(strategy.Horizon, unmarked, _timeProvider, cancellationToken).ConfigureAwait(false);
            foreach (var tail in gapped.Where(tail => tail.Checkpoint is null))
            {
                tail.Checkpoint = taken[tail.Key];
            }
        }

        var behind = false;
        var judgements = await HorizonCheckpoint.JudgeAsync(strategy.Horizon, gapped.ToDictionary(tail => tail.Key, tail => tail.Checkpoint!), cancellationToken).ConfigureAwait(false);
        foreach (var tail in gapped)
        {
            var checkpoint = tail.Checkpoint!;
            var judgement = judgements[tail.Key];
            if (judgement.IsFinal)
            {
                // Every position up to the ceiling is final: those seen are committed, the missing
                // ones never will be. The head moves to the ceiling, whatever number of gaps lay
                // below it, and extends from there; a gap past the ceiling is judged at the next look.
                tail.Checkpoint = null;
                if (checkpoint.Ceiling > tail.Head)
                {
                    LogGaveUp(tail.Key.TenantId, tail.Head, checkpoint.Ceiling, strategy.Kind);
                    var following = await strategy.Positions.ReadFollowingAsync(tail.Key, checkpoint.Ceiling, RefreshWindow, cancellationToken).ConfigureAwait(false);
                    Publish(tail, ContiguousPrefix.Extend(checkpoint.Ceiling, following));
                    behind |= following.Count > 0 && following[^1] > tail.Head;
                }
                else
                {
                    // The gap the checkpoint was taken over has filled; the one this look found
                    // lies past its ceiling, and gets a checkpoint of its own at the next look.
                    behind = true;
                }
            }
            else if (judgement.Holder is { } holder && !tail.Warned && checkpoint.GetAge() >= HoldWarning)
            {
                tail.Warned = true;
                LogHeld(tail.Key.TenantId, tail.Head, checkpoint.GetAge(), holder.SessionId, holder.LoginName, holder.StartedAt);
            }

            // Held, or not yet old enough: judged again at the fast interval, since the transaction
            // holding a gap is most often an append a moment from its commit, and a reader on
            // another instance than the writer waits on that judgement.
            behind |= !judgement.IsFinal;
        }

        return behind;
    }

    /// <summary>A tail for a sequence followed from now on, its numbering stopped when every sequence's is.</summary>
    private Tail CreateTail(SequenceKey sequence)
    {
        var tail = new Tail(this, sequence);
        if (Volatile.Read(ref _numberingStopped) is { } stopped)
        {
            tail.Numbering.Stop(stopped);
        }

        return tail;
    }

    private void Publish(Tail tail, long mark)
    {
        if (!tail.Followed.Publish(mark))
        {
            return;
        }

        TaskCompletionSource advanced;
        lock (_gate)
        {
            _advances++;
            advanced = _anyAdvanced;
            _anyAdvanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        advanced.TrySetResult();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The sequence heads are kept by the {Strategy} strategy for the life of this process.")]
    private partial void LogStrategy(HeadStrategyKind strategy);

    [LoggerMessage(Level = LogLevel.Error, Message = "Looking at the sequence heads failed; looking again after the interval.")]
    private partial void LogLookFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keeping the sequence heads in the gateway's table failed; the heads move on in this process, and the table catches up at a later look.")]
    private partial void LogKeepFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sequence of tenant {TenantId}: the positions missing after {Reached} up to {Ceiling} are final and were given up by the {Strategy} strategy.")]
    private partial void LogGaveUp(string? tenantId, long reached, long ceiling, HeadStrategyKind strategy);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sequence of tenant {TenantId}: the head has waited at {Reached} for {Age} on an open transaction that may still commit a missing position: session {SessionId}, login {LoginName}, begun {StartedAt}. Nothing past the head is delivered until it ends.")]
    private partial void LogHeld(string? tenantId, long reached, TimeSpan age, int? sessionId, string? loginName, DateTimeOffset? startedAt);

    /// <summary>How far one extension brought a head.</summary>
    private enum Extension
    {
        /// <summary>To the ceiling read at the start of the look.</summary>
        CaughtUp,

        /// <summary>Through a full window without a gap: more follows.</summary>
        FullWindow,

        /// <summary>To a gap below the ceiling, left to judge.</summary>
        Gapped,
    }

    /// <summary>One sequence's tail: its followed head and numbering, and the poller's checkpoint over the gap holding it.</summary>
    private sealed class Tail(SequenceTails owner, SequenceKey key) : IStoreTail
    {
        public SequenceKey Key => key;

        public FollowedHead Followed { get; } = new();

        public FollowedHead Numbering { get; } = new();

        public bool Begun { get; set; }

        /// <summary>Gets or sets the checkpoint over the gap holding the head; cleared, the gap is gone, and so is its warning.</summary>
        public HorizonCheckpoint? Checkpoint
        {
            get;
            set
            {
                field = value;
                if (value is null)
                {
                    Warned = false;
                }
            }
        }

        public bool Warned { get; set; }

        public long Head => Followed.Head;

        public long Numbered => Numbering.Head;

        public OrdinalSequencerException? NumberingStopped => Numbering.Stopped as OrdinalSequencerException;

        public ValueTask<long> RefreshAsync(CancellationToken cancellationToken) => owner.RefreshAsync(this, cancellationToken);

        public ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken) => Followed.WaitForAdvanceAsync(beyond, cancellationToken);

        public ValueTask<long> WaitForNumberedAsync(long through, CancellationToken cancellationToken) => Numbering.WaitForReachAsync(through, cancellationToken);
    }
}
