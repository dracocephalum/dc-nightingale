using System.Globalization;

using JasperFx.Events.Daemon;
using Microsoft.Data.SqlClient;
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
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="loggerFactory">Where the daemon's logger comes from.</param>
internal sealed class PolecatStoreTail(IDocumentStore store, string connectionString, ILoggerFactory loggerFactory) : IStoreTail, IHostedService, IAsyncDisposable
{
    /// <summary>How many rows past the mark one refresh looks at.</summary>
    private const int RefreshWindow = 100_000;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource _advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProjectionDaemon? _daemon;
    private Task? _loop;
    private long _head;

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

                advanced = _advanced.Task;
            }

            await advanced.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<long> RefreshAsync(CancellationToken cancellationToken)
    {
        // Row n after the mark is at position mark + n exactly when no number before it is missing,
        // so the highest such row is the last position every event up to which is committed. The
        // window bounds the scan when the poller is far behind; the poller closes the rest.
        var mark = Head;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = string.Format(
            CultureInfo.InvariantCulture,
            "WITH following AS (SELECT TOP (@window) seq_id, ROW_NUMBER() OVER (ORDER BY seq_id) AS n FROM {0} WHERE seq_id > @mark ORDER BY seq_id) SELECT ISNULL(MAX(seq_id), @mark) FROM following WHERE seq_id = @mark + n",
            EventsTable);
        command.Parameters.AddWithValue("@mark", mark);
        command.Parameters.AddWithValue("@window", RefreshWindow);
        var current = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        Publish(current);
        return Head;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var daemon = await ((DocumentStore)store).BuildProjectionDaemonAsync(logger: loggerFactory.CreateLogger<PolecatStoreTail>()).ConfigureAwait(false);
        await daemon.StartAllAsync().ConfigureAwait(false);
        _daemon = daemon;
        Publish(daemon.Tracker.HighWaterMark);
        _loop = RunAsync(daemon, _stopping.Token);
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

    private async Task RunAsync(IProjectionDaemon daemon, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                // The tracker's wait completes when the agent's mark reaches the number, without
                // touching the store: the agent's poll is the only query.
                await daemon.Tracker.WaitForHighWaterMark(Head + 1, null).WaitAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // The stop raced the wait; there is nothing left to follow.
                return;
            }

            Publish(daemon.Tracker.HighWaterMark);
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
}
