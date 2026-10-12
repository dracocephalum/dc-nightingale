using System.Globalization;

using Dracocephalum.Nightingale.Server.Data;
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
/// An event whose position the tail gave up as a gap and that committed late, below the progress,
/// is numbered by the next batch with the next ordinal of its key: by ordinal it follows what was
/// appended after it, and is never left out.
/// Under a sequence per tenant there is a sequence, a head and a progress row per tenant, and
/// the sequencer walks each in turn; positions are the tenant's own, so every batch is one
/// tenant's. How far each sequence is numbered is published in process as each batch commits,
/// for the subscriptions that wait on it; while another instance holds the lease, the follower
/// publishes it from the progress rows instead, between one ask for the lease and the next.
/// </summary>
/// <param name="leases">Where the lease is taken.</param>
/// <param name="sequences">The sequences to number, each with the head the sequencer never passes.</param>
/// <param name="follower">Keeps the numbering current while another instance holds the lease.</param>
/// <param name="registry">The instance id the lease is taken under.</param>
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="schemaName">The schema the event store's tables live in.</param>
/// <param name="progressSchemaName">The schema the gateway's own tables live in, the progress rows among them.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
/// <param name="leaseDuration">How long the lease lasts without renewal; <see cref="LeaseDuration"/> when not given. The renewal follows from it.</param>
internal sealed partial class OrdinalSequencer(
    ISubscriptionGroupStore leases,
    INumberedSequences sequences,
    NumberingFollower follower,
    SubscriptionGroupRegistry registry,
    string connectionString,
    string schemaName,
    string progressSchemaName,
    TimeProvider timeProvider,
    ILogger<OrdinalSequencer> logger,
    TimeSpan? leaseDuration = null) : IHostedService, IDisposable
{
    /// <summary>The lease every instance competes for.</summary>
    public const string LeaseName = "ordinals";

    /// <summary>How many positions one batch numbers at most.</summary>
    public const int BatchSize = 5_000;

    /// <summary>How long a lease lasts without renewal, after which another instance takes over, unless the instance was given another.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _leaseDuration = leaseDuration ?? LeaseDuration;

    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    /// <summary>
    /// Gets how often the holder renews, and how often a bystander asks again: a third of the
    /// lease, so that two renewals can fail before the lease is lost. Derived, never set, so a
    /// lease made shorter keeps the same room.
    /// </summary>
    public TimeSpan RenewInterval => _leaseDuration / 3;

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
    /// Numbers one batch of a sequence, from the position numbered through up to the head or the
    /// batch size, whichever comes first, and records the new progress in the same transaction.
    /// </summary>
    /// <param name="tenantId">The tenant whose sequence it is, or <see langword="null"/> for the shared sequence.</param>
    /// <param name="head">The sequence's high-water mark; nothing beyond it is numbered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The position numbered through after the batch.</returns>
    internal async Task<long> NumberBatchAsync(string? tenantId, long head, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var name = SequencerProgress.OrdinalsOf(tenantId);

        long from;
        await using (var progress = connection.CreateCommand())
        {
            progress.Transaction = transaction;
            progress.CommandText = string.Format(CultureInfo.InvariantCulture, "SELECT [Position] FROM {0} WITH (UPDLOCK, HOLDLOCK) WHERE [Name] = @name", OrdinalsTable);
            progress.Parameters.AddWithValue("@name", name);
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
                insert.CommandText = string.Format(CultureInfo.InvariantCulture, "INSERT INTO {0} ([Id], [Name], [Position]) VALUES (NEWID(), @name, 0)", OrdinalsTable);
                insert.Parameters.AddWithValue("@name", name);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Run even when the progress is at the head: a row committed late lies below the
        // progress and moves no head, and is found all the same, at the next advance or at the
        // next renewal on a quiet store. The progress row is written only when it moves.
        var to = Math.Max(from, Math.Min(head, from + BatchSize));
        await using (var number = connection.CreateCommand())
        {
            // The batch is every unnumbered row up to its end, found through the index over the
            // unnumbered rows: those between the progress and the end, and any below the
            // progress that committed late, after the tail had given their position up as a gap,
            // which an earlier batch therefore never saw. Each key's next ordinal is the last one
            // assigned to it plus one, found with one backward seek in the ordinal index; the
            // rows of the batch are then numbered in position order per key, so a late row gets
            // the next ordinal of its key when it is found and by ordinal follows what was
            // appended after it: late, never missing. The previous ordinal is -1 when a key is
            // new, so the first ordinal is 0. Under a sequence per tenant a position is only
            // unique with its tenant, so the batch is the tenant's rows alone.
            number.Transaction = transaction;
            number.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "WITH batch AS ("
                + " SELECT seq_id, tenant_id, category, [type],"
                + " ROW_NUMBER() OVER (PARTITION BY tenant_id, category ORDER BY seq_id) AS category_n,"
                + " ROW_NUMBER() OVER (PARTITION BY tenant_id, [type] ORDER BY seq_id) AS type_n"
                + " FROM {0} WHERE category_ordinal IS NULL AND seq_id <= @to{2}),"
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
                + " JOIN batch b ON b.seq_id = e.seq_id AND b.tenant_id = e.tenant_id"
                + " JOIN categories c ON c.tenant_id = b.tenant_id AND c.category = b.category"
                + " JOIN types t ON t.tenant_id = b.tenant_id AND t.[type] = b.[type];"
                + "{1}",
                EventsTable,
                to > from ? string.Format(CultureInfo.InvariantCulture, " UPDATE {0} SET [Position] = @to WHERE [Name] = @name", OrdinalsTable) : string.Empty,
                tenantId is null ? string.Empty : " AND tenant_id = @tenant");
            number.Parameters.AddWithValue("@to", to);
            number.Parameters.AddWithValue("@name", name);
            if (tenantId is not null)
            {
                number.Parameters.AddWithValue("@tenant", tenantId);
            }

            var rows = await number.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            LogNumbered(name, to, rows - 1);
        }

        return to;
    }

    // The schema name comes from the store's options, never from a request; it is bracketed as an
    // identifier all the same.
    private string Schema => "[" + schemaName.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private string EventsTable => Schema + ".[pc_events]";

    private string OrdinalsTable => "[" + progressSchemaName.Replace("]", "]]", StringComparison.Ordinal) + "].[" + nameof(SequencerProgress) + "]";

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
                    var holder = await leases.AcquireLeaseAsync(LeaseName, owner, null, _leaseDuration, stopping).ConfigureAwait(false);
                    if (holder is not null)
                    {
                        // Another instance numbers; this one follows its progress until it is
                        // time to ask again.
                        holding = false;
                        await follower.FollowAsync(RenewInterval, stopping).ConfigureAwait(false);
                        continue;
                    }

                    if (!holding)
                    {
                        holding = true;
                        LogTookOver(owner);
                    }

                    // The count is read before the heads are, so a head that moves during the pass
                    // is not waited on as if it had not.
                    var seen = sequences.Advances;
                    if (!await CatchUpAsync(owner, stopping).ConfigureAwait(false))
                    {
                        // The lease was lost mid-way; the loop follows the new holder's
                        // progress and asks again after the interval.
                        holding = false;
                        await follower.FollowAsync(RenewInterval, stopping).ConfigureAwait(false);
                        continue;
                    }

                    // Caught up: wait for a head, but no longer than the renewal interval, so the
                    // lease is renewed by the next acquire while the store is quiet.
                    try
                    {
                        await sequences.WaitForAnyAdvanceAsync(seen, stopping).WaitAsync(RenewInterval, timeProvider, stopping).ConfigureAwait(false);
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
                catch (Exception exception)
                {
                    // A cancellation nobody asked for is a failure like any other: logged and
                    // tried again. Left to pass, it would end this loop with nobody the wiser,
                    // and nothing would be numbered from then on.
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

    /// <summary>Numbers every sequence in batches until each has reached its head, renewing the lease along the way.</summary>
    /// <returns>Whether every sequence was caught up; false when the lease was lost.</returns>
    private async Task<bool> CatchUpAsync(string owner, CancellationToken stopping)
    {
        var renewed = timeProvider.GetTimestamp();
        foreach (var sequence in await sequences.ListAsync(stopping).ConfigureAwait(false))
        {
            while (true)
            {
                var head = sequence.Tail.Head;
                var numbered = await NumberBatchAsync(sequence.TenantId, head, stopping).ConfigureAwait(false);
                sequence.Numbering.Publish(numbered);
                if (numbered >= head)
                {
                    break;
                }

                if (timeProvider.GetElapsedTime(renewed) >= RenewInterval)
                {
                    if (await leases.AcquireLeaseAsync(LeaseName, owner, null, _leaseDuration, stopping).ConfigureAwait(false) is not null)
                    {
                        return false;
                    }

                    renewed = timeProvider.GetTimestamp();
                }
            }
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "This instance ({Owner}) numbers the virtual streams.")]
    private partial void LogTookOver(string owner);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Numbered {Sequence} through position {Through}: {Rows} rows.")]
    private partial void LogNumbered(string sequence, long through, int rows);

    [LoggerMessage(Level = LogLevel.Error, Message = "Numbering the virtual streams failed; retrying after the interval.")]
    private partial void LogFailed(Exception exception);
}
