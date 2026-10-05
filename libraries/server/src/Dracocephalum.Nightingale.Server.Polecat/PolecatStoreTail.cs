using Dracocephalum.Nightingale.Server.Polecat.Data;
using JasperFx.Events.Daemon;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polecat;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The one tailer of the process. It runs the store's own high-water agent, the piece of its
/// async daemon that finds the position up to which every event is committed, and nothing else
/// of the daemon, because the gateway registers no projections. The agent advances only through
/// committed, gap-free sequence numbers, persists the mark in the store's progression table, and
/// polls on the daemon's cadence: the fast interval while the head moves, the slow one when it is
/// quiet. Every subscription in the process waits on this one tail rather than polling the store
/// itself. Waking the agent the moment this process appends, instead of waiting out an interval,
/// is recorded in TODO.md.
/// </summary>
/// <param name="store">The store.</param>
/// <param name="events">Makes the mirror of the store's events table, over the main connection.</param>
/// <param name="loggerFactory">Where the daemon's logger comes from.</param>
/// <param name="quietWait">How long one wait on the store's tracker lasts before the tracker ends it with a timeout; a minute when not given.</param>
internal sealed partial class PolecatStoreTail(IDocumentStore store, IDbContextFactory<EventsDbContext> events, ILoggerFactory loggerFactory, TimeSpan? quietWait = null) : IStoreTail, IHostedService, IAsyncDisposable
{
    /// <summary>How many rows past the mark one refresh looks at.</summary>
    private const int RefreshWindow = 10_000;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly ILogger<PolecatStoreTail> _logger = loggerFactory.CreateLogger<PolecatStoreTail>();
    private TaskCompletionSource _advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProjectionDaemon? _daemon;
    private Task? _loop;
    private long _head;
    private InvalidOperationException? _failure;

    /// <inheritdoc/>
    public long Head => Volatile.Read(ref _head);

    /// <inheritdoc/>
    public async ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task advanced;
            lock (_gate)
            {
                if (_head > beyond)
                {
                    return _head;
                }

                // A tail that has stopped following never advances again. Whoever waits on it
                // is told so, now and from here on, rather than left waiting for good.
                if (_failure is not null)
                {
                    throw _failure;
                }

                advanced = _advanced.Task;
            }

            await advanced.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

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
        Publish(ContiguousPrefix.After(mark, following));
        return Head;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var daemon = await ((DocumentStore)store).BuildProjectionDaemonAsync(logger: loggerFactory.CreateLogger<PolecatStoreTail>()).ConfigureAwait(false);
        await daemon.StartAllAsync().ConfigureAwait(false);
        _daemon = daemon;
        Publish(daemon.Tracker.HighWaterMark);
        _loop = FollowAsync(
            (beyond, stopping) => daemon.Tracker.WaitForHighWaterMark(beyond + 1, quietWait).WaitAsync(stopping),
            () => daemon.Tracker.HighWaterMark,
            _stopping.Token);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
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

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
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
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// Follows the mark until told to stop. The wait completes when the agent's mark passes the
    /// number, without touching the store: the agent's poll is the only query. A wait that runs
    /// out on a quiet store is begun again. Ending for any
    /// other reason than being stopped is a failure, said out loud and handed to everyone who
    /// waits on the head: the head would never move again, and a subscription that went on
    /// waiting for it would look alive and deliver nothing.
    /// </summary>
    /// <param name="waitBeyond">Completes when the mark is beyond a position.</param>
    /// <param name="mark">Reads the mark.</param>
    /// <param name="stopping">Cancelled when the tail is stopped.</param>
    /// <returns>A task that completes when the tail has stopped following.</returns>
    internal async Task FollowAsync(Func<long, CancellationToken, Task> waitBeyond, Func<long> mark, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await waitBeyond(Head, stopping).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The tracker's wait ends with a timeout when the mark has not moved in the
                    // time it was given, a minute: a store nobody appended to. That is no failure and no
                    // reason to stop following; the mark is read and the wait begun again.
                }

                Publish(mark());
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

    // The schema name comes from the store's options, never from a request; it is bracketed as an
    // identifier all the same.
    private string EventsTable =>
        $"[{store.Options.DatabaseSchemaName.Replace("]", "]]", StringComparison.Ordinal)}].[pc_events]";

    private void Publish(long mark)
    {
        TaskCompletionSource? advanced = null;
        lock (_gate)
        {
            if (mark > _head)
            {
                Volatile.Write(ref _head, mark);
                advanced = _advanced;
                _advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        advanced?.TrySetResult();
    }

    private void Fail(Exception exception)
    {
        LogStoppedFollowing(exception);
        var failure = new InvalidOperationException("The server has stopped following the store's head, so nothing appended from now on is delivered live or numbered. The cause is in the inner exception and in the server's log; the server needs a restart.", exception);
        TaskCompletionSource advanced;
        lock (_gate)
        {
            _failure = failure;
            advanced = _advanced;
        }

        // Observed here, so that a tail nobody happened to be waiting on leaves no unobserved fault.
        advanced.TrySetException(failure);
        _ = advanced.Task.Exception;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The tail has stopped following the store's head without being stopped. Live delivery and ordinal numbering have ended; every call waiting on them fails with this cause. The server needs a restart.")]
    private partial void LogStoppedFollowing(Exception exception);
}
