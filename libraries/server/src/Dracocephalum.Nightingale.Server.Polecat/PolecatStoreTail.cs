using Dracocephalum.Nightingale.Server.Polecat.Data;
using JasperFx.Events.Daemon;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polecat;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The one tailer of the process over a shared sequence. It runs the store's own high-water
/// agent, the piece of its async daemon that finds the position up to which every event is
/// committed, and nothing else of the daemon, because the gateway registers no projections. The
/// agent advances only through committed, gap-free sequence numbers, persists the mark in the
/// store's progression table, and polls on the daemon's cadence: the fast interval while the head
/// moves and is still short of the last number taken, the slow one once it has caught up or
/// while a gap holds it, and is woken ahead of either by an append this process commits, through
/// the <see cref="AppendSignal"/> the store was given as the daemon's wake-up.
/// Every subscription in the process waits on this one tail rather than polling the store itself.
/// Under a sequence per tenant the store's agent follows a mark that is nobody's, and
/// <see cref="PolecatTenantTails"/> follows one per tenant instead.
/// </summary>
/// <param name="store">The store.</param>
/// <param name="events">Makes the mirror of the store's events table, over the main connection.</param>
/// <param name="loggerFactory">Where the daemon's logger comes from.</param>
/// <param name="quietWait">How long one wait on the store's tracker lasts before the tracker ends it with a timeout; a minute when not given.</param>
internal sealed partial class PolecatStoreTail(IDocumentStore store, IDbContextFactory<EventsDbContext> events, ILoggerFactory loggerFactory, TimeSpan? quietWait = null) : IStoreTail, IHostedService, IAsyncDisposable
{
    /// <summary>How many rows past the mark one refresh looks at.</summary>
    private const int RefreshWindow = 10_000;

    private readonly FollowedHead _head = new();
    private readonly FollowedHead _numbering = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ILogger<PolecatStoreTail> _logger = loggerFactory.CreateLogger<PolecatStoreTail>();
    private IProjectionDaemon? _daemon;
    private Task? _loop;
    private bool _stopped;
    private bool _disposed;

    /// <inheritdoc/>
    public long Head => _head.Head;

    /// <inheritdoc/>
    public long Numbered => _numbering.Head;

    /// <summary>Gets the numbering as it is followed: where the sequencer, or whoever follows it for this process, publishes how far it has come.</summary>
    internal FollowedHead Numbering => _numbering;

    /// <inheritdoc/>
    public ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken) => _head.WaitForAdvanceAsync(beyond, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<long> WaitForNumberedAsync(long through, CancellationToken cancellationToken) => _numbering.WaitForReachAsync(through, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<long> RefreshAsync(CancellationToken cancellationToken)
    {
        // The positions after the mark, as far as they follow it one by one: the last position
        // every event up to which is committed. The window bounds the read when the poller is far
        // behind; the poller closes the rest.
        var mark = Head;
        await using var context = await events.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var following = await context.EventRows.AsNoTracking()
            .Where(row => row.SeqId > mark)
            .OrderBy(row => row.SeqId)
            .Select(row => row.SeqId)
            .Take(RefreshWindow)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        _head.Publish(ContiguousPrefix.Extend(mark, following));
        return Head;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var daemon = await ((DocumentStore)store).BuildProjectionDaemonAsync(logger: loggerFactory.CreateLogger<PolecatStoreTail>()).ConfigureAwait(false);
        await daemon.StartAllAsync().ConfigureAwait(false);
        _daemon = daemon;
        _head.Publish(daemon.Tracker.HighWaterMark);
        _loop = FollowAsync(
            (beyond, stopping) => daemon.Tracker.WaitForHighWaterMark(beyond + 1, quietWait).WaitAsync(stopping),
            () => daemon.Tracker.HighWaterMark,
            _stopping.Token);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A host can be stopped by two parties at once, whoever asked for the stop and the
        // program's own wait for shutdown, and disposed by one while the other still stops it.
        // Stopping and disposing therefore take turns, each does its work once, and disposing
        // stops first: the daemon is never stopped twice, nor after it was disposed.
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopOnceAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopOnceAsync().ConfigureAwait(false);
            _disposed = true;
            switch (_daemon)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                default:
                    break;
            }

            _daemon = null;
            _stopping.Dispose();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopOnceAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;

        // Our loop is drained first, so nothing of ours waits on the tracker while the daemon stops.
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
            _loop = null;
        }

        if (_daemon is not null)
        {
            await _daemon.StopAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Follows the mark until told to stop. The wait completes when the agent's mark passes the
    /// number, without touching the store: the agent's poll is the only query. A wait that runs
    /// out on a quiet store is begun again. Ending for any
    /// other reason than being stopped is a failure, said out loud and handed to everyone who
    /// waits on the head: the head would never move again, and a subscription that went on
    /// waiting for it would look alive and deliver nothing.
    /// </summary>
    /// <param name="waiter">Completes when the mark is beyond a position.</param>
    /// <param name="mark">Reads the mark.</param>
    /// <param name="stopping">Cancelled when the tail is stopped.</param>
    /// <returns>A task that completes when the tail has stopped following.</returns>
    internal async Task FollowAsync(Func<long, CancellationToken, Task> waiter, Func<long> mark, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await waiter(Head, stopping).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The tracker's wait ends with a timeout when the mark has not moved in the
                    // time it was given, a minute: a store nobody appended to. That is no failure and no
                    // reason to stop following; the mark is read and the wait begun again.
                }

                _head.Publish(mark());
            }
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // Stopped: whatever the wait threw as the daemon went down under it is the stop.
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void Fail(Exception exception)
    {
        LogStoppedFollowing(exception);
        var failure = new InvalidOperationException("The server has stopped following the store's head, so nothing appended from now on is delivered live or numbered. The cause is in the inner exception and in the server's log; the server needs a restart.", exception);
        _head.Fail(failure);
        _numbering.Fail(failure);
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The tail has stopped following the store's head without being stopped. Live delivery and ordinal numbering have ended; every call waiting on them fails with this cause. The server needs a restart.")]
    private partial void LogStoppedFollowing(Exception exception);
}
