using System.Globalization;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The sequencer: one per cluster, on the same lease persistent-subscription groups use, walking
/// the events table behind the high-water mark in batches and giving every event its place within
/// its category stream and its event-type stream, each batch continuing from the last ordinal of
/// each key. The append path is untouched, because numbering at append time would serialize every
/// append to a category; the price is that ordinals lag the high-water mark by this loop's cadence,
/// which is the tail's. A batch and the progress row are one transaction, and the progress row is
/// read under an update lock, so a second sequencer whose lease overlapped a takeover continues
/// from what the first committed rather than numbering anything twice. An event archived or removed
/// after numbering keeps or vacates its ordinal, and the next ordinal of the key is never reused.
/// </summary>
/// <param name="leases">Where the lease is taken.</param>
/// <param name="tail">The head the sequencer never passes.</param>
/// <param name="registry">The instance id the lease is taken under.</param>
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="schemaName">The schema the tables live in.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class OrdinalSequencer(
    IGroupStore leases,
    IStoreTail tail,
    GroupRegistry registry,
    string connectionString,
    string schemaName,
    TimeProvider timeProvider,
    ILogger<OrdinalSequencer> logger) : IHostedService, IDisposable
{
    /// <summary>The lease every instance competes for.</summary>
    public const string LeaseName = "ordinals";

    /// <summary>How many positions one batch numbers at most.</summary>
    public const int BatchSize = 5_000;

    /// <summary>How long a lease lasts without renewal, after which another instance takes over.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>How often the holder renews, and how often a bystander asks again.</summary>
    public static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = RunAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
            _loop = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// Numbers one batch, from the position numbered through up to the head or the batch size,
    /// whichever comes first, and records the new progress in the same transaction.
    /// </summary>
    /// <param name="head">The high-water mark; nothing beyond it is numbered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The position numbered through after the batch.</returns>
    internal async Task<long> NumberBatchAsync(long head, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long from;
        await using (var progress = connection.CreateCommand())
        {
            progress.Transaction = transaction;
            progress.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "SELECT numbered_through FROM {0} WITH (UPDLOCK, HOLDLOCK) WHERE id = 1",
                OrdinalsTable);
            var value = await progress.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is long known)
            {
                from = known;
            }
            else
            {
                from = 0;
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = string.Format(CultureInfo.InvariantCulture, "INSERT INTO {0} (id, numbered_through) VALUES (1, 0)", OrdinalsTable);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (from >= head)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return from;
        }

        var to = Math.Min(head, from + BatchSize);
        await using (var number = connection.CreateCommand())
        {
            // Each key's next ordinal is the last one assigned to it plus one, found with one
            // backward seek in the ordinal index; the rows of the batch are then numbered in
            // position order per key. The previous ordinal is -1 when a key is new, so the first
            // ordinal is 0.
            number.Transaction = transaction;
            number.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "WITH batch AS ("
                + " SELECT seq_id, tenant_id, category, [type],"
                + " ROW_NUMBER() OVER (PARTITION BY tenant_id, category ORDER BY seq_id) AS category_n,"
                + " ROW_NUMBER() OVER (PARTITION BY tenant_id, [type] ORDER BY seq_id) AS type_n"
                + " FROM {0} WHERE seq_id > @from AND seq_id <= @to),"
                + " categories AS ("
                + " SELECT k.tenant_id, k.category, ISNULL(last.category_ordinal, -1) AS last_ordinal"
                + " FROM (SELECT DISTINCT tenant_id, category FROM batch) k"
                + " OUTER APPLY (SELECT TOP (1) e.category_ordinal FROM {0} e WHERE e.tenant_id = k.tenant_id AND e.category = k.category AND e.category_ordinal IS NOT NULL ORDER BY e.category_ordinal DESC) last),"
                + " types AS ("
                + " SELECT k.tenant_id, k.[type], ISNULL(last.type_ordinal, -1) AS last_ordinal"
                + " FROM (SELECT DISTINCT tenant_id, [type] FROM batch) k"
                + " OUTER APPLY (SELECT TOP (1) e.type_ordinal FROM {0} e WHERE e.tenant_id = k.tenant_id AND e.[type] = k.[type] AND e.type_ordinal IS NOT NULL ORDER BY e.type_ordinal DESC) last)"
                + " UPDATE e SET category_ordinal = c.last_ordinal + b.category_n, type_ordinal = t.last_ordinal + b.type_n"
                + " FROM {0} e"
                + " JOIN batch b ON b.seq_id = e.seq_id"
                + " JOIN categories c ON c.tenant_id = b.tenant_id AND c.category = b.category"
                + " JOIN types t ON t.tenant_id = b.tenant_id AND t.[type] = b.[type];"
                + " UPDATE {1} SET numbered_through = @to WHERE id = 1",
                EventsTable,
                OrdinalsTable);
            number.Parameters.AddWithValue("@from", from);
            number.Parameters.AddWithValue("@to", to);
            var rows = await number.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            LogNumbered(to, rows - 1);
        }

        return to;
    }

    // The schema name comes from the store's options, never from a request; it is bracketed as an
    // identifier all the same.
    private string Schema => "[" + schemaName.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private string EventsTable => Schema + ".[pc_events]";

    private string OrdinalsTable => Schema + ".[" + NightingaleTablesFeature.OrdinalsTable + "]";

    private async Task RunAsync(CancellationToken stopping)
    {
        var owner = registry.InstanceId;
        var holding = false;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    var holder = await leases.AcquireLeaseAsync(LeaseName, owner, LeaseDuration, stopping).ConfigureAwait(false);
                    if (holder is not null)
                    {
                        holding = false;
                        await Task.Delay(RenewInterval, timeProvider, stopping).ConfigureAwait(false);
                        continue;
                    }

                    if (!holding)
                    {
                        holding = true;
                        LogTookOver(owner);
                    }

                    var numbered = await CatchUpAsync(owner, stopping).ConfigureAwait(false);
                    if (numbered < 0)
                    {
                        // The lease was lost mid-way; the loop asks again after the interval.
                        holding = false;
                        await Task.Delay(RenewInterval, timeProvider, stopping).ConfigureAwait(false);
                        continue;
                    }

                    // Caught up: wait for the tail, but no longer than the renewal interval, so the
                    // lease is renewed by the next acquire while the store is quiet.
                    try
                    {
                        await tail.WaitForAdvanceAsync(numbered, stopping).AsTask().WaitAsync(RenewInterval, timeProvider, stopping).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Quiet; renew.
                    }
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFailed(exception);
                    holding = false;
                    try
                    {
                        await Task.Delay(RenewInterval, timeProvider, stopping).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            if (holding)
            {
                try
                {
                    await leases.ReleaseLeaseAsync(LeaseName, owner, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFailed(exception);
                }
            }
        }
    }

    /// <summary>Numbers batches until the head is reached, renewing the lease along the way.</summary>
    /// <returns>The position numbered through, or -1 when the lease was lost.</returns>
    private async Task<long> CatchUpAsync(string owner, CancellationToken stopping)
    {
        var renewed = timeProvider.GetTimestamp();
        while (true)
        {
            var head = tail.Head;
            var numbered = await NumberBatchAsync(head, stopping).ConfigureAwait(false);
            if (numbered >= head)
            {
                return numbered;
            }

            if (timeProvider.GetElapsedTime(renewed) >= RenewInterval)
            {
                if (await leases.AcquireLeaseAsync(LeaseName, owner, LeaseDuration, stopping).ConfigureAwait(false) is not null)
                {
                    return -1;
                }

                renewed = timeProvider.GetTimestamp();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "This instance ({Owner}) numbers the virtual streams.")]
    private partial void LogTookOver(string owner);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Numbered events through position {Through}: {Rows} rows.")]
    private partial void LogNumbered(long through, int rows);

    [LoggerMessage(Level = LogLevel.Error, Message = "Numbering the virtual streams failed; retrying after the interval.")]
    private partial void LogFailed(Exception exception);
}
