using System.Collections.Concurrent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The tails of a store with a sequence per tenant: a high-water mark per tenant, followed by one
/// poller for the process. The store's own daemon keeps a mark per tenant too, but only for the
/// tenants its projections are assigned, of which the gateway has none, and its detector is not
/// reachable from outside (<c>UPSTREAM.md</c>); so the poller runs the detection itself, in the
/// store's terms. A tenant's mark is the last position every event of the tenant up to which is
/// committed, found by extending the mark through the positions that follow it one by one; a gap
/// that has held the mark for the store's stale threshold is given up as a transaction that will
/// never commit, the mark moving to the last position appended before the threshold, as the
/// store's agent does. One round trip per interval finds which polled tenants moved, and only
/// those are read; the interval is the store's fast one while something moved and its slow one
/// when nothing did, and an append made through this process cuts either short: the poller waits
/// on the append signal, so the look follows the commit at once. A tenant is polled from the first
/// time its tail is asked for, for the life of the process, as its stores are kept. A look that
/// fails is logged and taken again at the next interval: the heads stay where they were, and
/// whoever waits on them waits on.
/// </summary>
/// <param name="positions">The events table's positions, as the poller reads them.</param>
/// <param name="appended">Pulsed after each append this process commits; a wait between looks ends at a pulse.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
/// <param name="fastInterval">How long the poller waits after a look that found movement; <see cref="FastInterval"/> when not given.</param>
/// <param name="slowInterval">How long it waits after a look that found none; <see cref="SlowInterval"/> when not given.</param>
/// <param name="staleAfter">How long a gap holds a mark before it is given up; <see cref="StaleAfter"/> when not given.</param>
internal sealed partial class PolecatTenantTails(
    ITenantEventPositions positions,
    AppendSignal appended,
    TimeProvider timeProvider,
    ILogger<PolecatTenantTails> logger,
    TimeSpan? fastInterval = null,
    TimeSpan? slowInterval = null,
    TimeSpan? staleAfter = null) : IHostedService, IAsyncDisposable
{
    /// <summary>How long the poller waits after a look that found movement: the store's fast polling time.</summary>
    public static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the poller waits after a look that found none: the store's slow polling time.</summary>
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a gap holds a mark before it is given up as abandoned: the store's stale sequence threshold.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    /// <summary>How many positions past a mark one extension reads.</summary>
    public const int RefreshWindow = 10_000;

    private readonly TimeSpan _fast = fastInterval ?? FastInterval;
    private readonly TimeSpan _slow = slowInterval ?? SlowInterval;
    private readonly TimeSpan _staleAfter = staleAfter ?? StaleAfter;
    private readonly ConcurrentDictionary<string, TenantTail> _tails = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource _anyAdvanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _advances;
    private Task? _loop;
    private bool _disposed;

    /// <summary>Gets how many times any tenant's head has moved; what <see cref="WaitForAnyAdvanceAsync"/> is asked against.</summary>
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

    /// <summary>Gets the tenants polled: those whose tail has been asked for.</summary>
    public IReadOnlyCollection<string> PolledTenants => _tails.Keys.ToArray();

    /// <summary>The tail of a tenant, polled from now on.</summary>
    /// <param name="storeTenantId">The id the store keeps for the tenant.</param>
    /// <returns>The tail.</returns>
    public IStoreTail For(string storeTenantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeTenantId);
        return _tails.GetOrAdd(storeTenantId, id => new TenantTail(this, id));
    }

    /// <summary>The numbering of a tenant's sequence as it is followed: where the sequencer, or whoever follows it for this process, publishes how far it has come.</summary>
    /// <param name="storeTenantId">The id the store keeps for the tenant.</param>
    /// <returns>The followed numbering.</returns>
    internal FollowedHead NumberingOf(string storeTenantId) => ((TenantTail)For(storeTenantId)).Numbering;

    /// <summary>Waits until any tenant's head has moved since a count of advances was read; for whoever follows every tenant, the sequencer.</summary>
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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = PollAsync(_stopping.Token);
        return Task.CompletedTask;
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
    }

    /// <summary>
    /// One look at every polled tenant: which moved past its head, by one read over all of them,
    /// then each of those extended. The poller looks on its cadence; a test looks by hand.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether any head moved.</returns>
    internal async Task<bool> LookAsync(CancellationToken cancellationToken)
    {
        var tails = _tails.Values.ToArray();
        if (tails.Length == 0)
        {
            return false;
        }

        var ceilings = await positions.ReadCeilingsAsync(tails.Select(tail => tail.TenantId).ToArray(), cancellationToken).ConfigureAwait(false);
        var moved = false;
        foreach (var tail in tails)
        {
            if (ceilings.TryGetValue(tail.TenantId, out var ceiling) && ceiling > tail.Followed.Head)
            {
                moved |= await ExtendAsync(tail, true, cancellationToken).ConfigureAwait(false);
            }
        }

        return moved;
    }

    private async Task PollAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var moved = false;
            try
            {
                moved = await LookAsync(stopping).ConfigureAwait(false);
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
                await appended.WaitAsync(moved ? _fast : _slow, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Brings a tenant's head up to date now, through the positions that follow it without a gap; a gap is left to the poller.</summary>
    private async ValueTask<long> RefreshAsync(TenantTail tail, CancellationToken cancellationToken)
    {
        await ExtendAsync(tail, false, cancellationToken).ConfigureAwait(false);
        return tail.Followed.Head;
    }

    /// <summary>
    /// Extends a tenant's head through the committed positions that follow it one by one, and,
    /// when asked to, past a gap that has held it longer than the threshold.
    /// </summary>
    /// <returns>Whether the head moved.</returns>
    private async Task<bool> ExtendAsync(TenantTail tail, bool giveUpAbandoned, CancellationToken cancellationToken)
    {
        var mark = tail.Followed.Head;
        var following = await positions.ReadFollowingAsync(tail.TenantId, mark, RefreshWindow, cancellationToken).ConfigureAwait(false);
        var reached = ContiguousPrefix.Extend(mark, following);
        var gapFollows = following.Count > 0 && following[^1] > reached;
        if (gapFollows && giveUpAbandoned)
        {
            // A gap right after the head: a number taken by a transaction that has not committed.
            // Its clock runs from the look that first saw positions beyond the head. Held past the
            // threshold, it is one that never will commit, and the head moves to the last position
            // appended before the threshold, the store's own rule; positions appended within it
            // may still be waiting on a number below them, so they are left for a later look.
            var now = timeProvider.GetUtcNow();
            tail.GapSince ??= now;
            if (now - tail.GapSince.Value >= _staleAfter)
            {
                var past = await positions.FindLastAppendedBeforeAsync(tail.TenantId, reached, now - _staleAfter, cancellationToken).ConfigureAwait(false);
                if (past is { } beyond)
                {
                    LogGaveUpGap(tail.TenantId, reached, beyond);
                    reached = ContiguousPrefix.Extend(beyond, await positions.ReadFollowingAsync(tail.TenantId, beyond, RefreshWindow, cancellationToken).ConfigureAwait(false));
                    gapFollows = false;
                }
            }
        }

        if (giveUpAbandoned && !gapFollows)
        {
            tail.GapSince = null;
        }

        if (reached <= mark)
        {
            return false;
        }

        Publish(tail, reached);
        return true;
    }

    private void Publish(TenantTail tail, long mark)
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Looking at the tenants' heads failed; looking again after the interval.")]
    private partial void LogLookFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {TenantId}: a gap after position {Mark} held the head longer than the threshold and was given up; the head moved to {Beyond}.")]
    private partial void LogGaveUpGap(string tenantId, long mark, long beyond);

    /// <summary>One tenant's tail: its followed head, and the poller's note of when a gap first held it.</summary>
    private sealed class TenantTail(PolecatTenantTails owner, string tenantId) : IStoreTail
    {
        public string TenantId => tenantId;

        public FollowedHead Followed { get; } = new();

        public FollowedHead Numbering { get; } = new();

        public DateTimeOffset? GapSince { get; set; }

        public long Head => Followed.Head;

        public long Numbered => Numbering.Head;

        public ValueTask<long> RefreshAsync(CancellationToken cancellationToken) => owner.RefreshAsync(this, cancellationToken);

        public ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken) => Followed.WaitForAdvanceAsync(beyond, cancellationToken);

        public ValueTask<long> WaitForNumberedAsync(long through, CancellationToken cancellationToken) => Numbering.WaitForReachAsync(through, cancellationToken);
    }
}
