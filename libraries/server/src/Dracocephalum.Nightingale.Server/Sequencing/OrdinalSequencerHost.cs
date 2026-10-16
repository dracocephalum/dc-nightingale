using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The host of the ordinal sequencer: one per cluster, on the same lease persistent-subscription
/// groups use, walking every sequence behind its head in batches, each sequenced by the backend's
/// <see cref="IOrdinalSequencer"/>, which gives every event of the range its place, as links in the
/// gateway's own table, and records the progress in the same transaction. The append path is
/// untouched, because numbering at append time would serialize every append to a category; the
/// price is that ordinals lag the head by this loop's cadence, which is the tails'. Nothing commits
/// below the head once it has passed, since the tails pass a missing position only once it is
/// final, by the transaction horizon, or by the best-effort strategy's wait over reads that wait on
/// an open row; so a range numbered is never looked at again (<c>PENDING.md</c>, *Scrubs*, for
/// what that wait can miss). Under a sequence per tenant there is a sequence, a head and a
/// progress row per tenant, and the sequencer walks each in turn. How far each sequence is
/// numbered is published in process as each batch commits, for the subscriptions that wait on
/// it; while another instance holds the lease, the follower publishes it from the progress rows
/// instead, between one ask for the lease and the next. Every backend runs this host; only the
/// sequencer, a batch, is its own.
/// </summary>
/// <param name="leases">Where the lease is taken.</param>
/// <param name="sequences">The sequences to number, each with the head the sequencer never passes.</param>
/// <param name="follower">Keeps the numbering current while another instance holds the lease.</param>
/// <param name="registry">The instance id the lease is taken under.</param>
/// <param name="sequencer">Sequences a batch, in the backend's own statement.</param>
/// <param name="faults">Where a sequence is recorded as faulted, and as numbering again, for every instance.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
/// <param name="leaseDuration">How long the lease lasts without renewal; <see cref="OrdinalSequencing.LeaseDuration"/> when not given. The renewal follows from it.</param>
internal sealed partial class OrdinalSequencerHost(
    ISubscriptionGroupStore leases,
    INumberedSequences sequences,
    OrdinalSequencingFollower follower,
    SubscriptionGroupRegistry registry,
    IOrdinalSequencer sequencer,
    IOrdinalSequencingFaults faults,
    TimeProvider timeProvider,
    ILogger<OrdinalSequencerHost> logger,
    TimeSpan? leaseDuration = null) : IHostedService, IDisposable
{
    private readonly TimeSpan _leaseDuration = leaseDuration ?? OrdinalSequencing.LeaseDuration;

    /// <summary>How many batches of a sequence fail in a row before its numbering is stopped as faulted.</summary>
    public const int FaultAfter = 3;

    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<SequenceKey, int> _failures = [];
    private readonly HashSet<SequenceKey> _faulted = [];
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
                    var holder = await leases.AcquireLeaseAsync(OrdinalSequencing.LeaseName, owner, null, _leaseDuration, stopping).ConfigureAwait(false);
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

                        // What an earlier holder recorded as faulted is cleared by this one as it
                        // numbers it again.
                        foreach (var tenantId in await faults.ReadFaultedAsync(stopping).ConfigureAwait(false))
                        {
                            _faulted.Add(new SequenceKey(tenantId));
                        }
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
                    await leases.ReleaseLeaseAsync(OrdinalSequencing.LeaseName, owner, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFailed(exception);
                }
            }
        }
    }

    /// <summary>
    /// Numbers every sequence in batches until each has reached its head, renewing the lease along
    /// the way. A sequence whose batch fails is left for the next pass while the others go on; once
    /// it has failed <see cref="FaultAfter"/> times in a row its numbering is stopped with the fault,
    /// here and, through its progress row, on every instance, and the first batch that succeeds
    /// lifts it again.
    /// </summary>
    /// <returns>Whether every sequence was caught up or left for the next pass; false when the lease was lost.</returns>
    private async Task<bool> CatchUpAsync(string owner, CancellationToken stopping)
    {
        var renewed = timeProvider.GetTimestamp();
        foreach (var sequence in await sequences.ListAsync(stopping).ConfigureAwait(false))
        {
            try
            {
                while (true)
                {
                    var head = sequence.Tail.Head;
                    var numbered = await sequencer.SequenceBatchAsync(sequence.TenantId, head, stopping).ConfigureAwait(false);
                    sequence.Numbering.Publish(numbered);
                    if (numbered >= head)
                    {
                        break;
                    }

                    if (timeProvider.GetElapsedTime(renewed) >= RenewInterval)
                    {
                        if (await leases.AcquireLeaseAsync(OrdinalSequencing.LeaseName, owner, null, _leaseDuration, stopping).ConfigureAwait(false) is not null)
                        {
                            return false;
                        }

                        renewed = timeProvider.GetTimestamp();
                    }
                }
            }
            catch (Exception exception) when (!stopping.IsCancellationRequested)
            {
                await FailAsync(sequence, exception, stopping).ConfigureAwait(false);
                continue;
            }

            await RecoverAsync(sequence, stopping).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Counts a failed batch, and stops the sequence's numbering with the fault once the count reaches the threshold.</summary>
    private async Task FailAsync(NumberedSequence sequence, Exception exception, CancellationToken stopping)
    {
        var key = new SequenceKey(sequence.TenantId);
        var failures = _failures[key] = _failures.GetValueOrDefault(key) + 1;
        LogBatchFailed(exception, sequence.TenantId ?? SharedSequence, failures);
        if (failures < FaultAfter || sequence.Numbering.Stopped is not null)
        {
            return;
        }

        sequence.Numbering.Stop(new OrdinalSequencerFaultedException(exception.Message, exception));
        _faulted.Add(key);
        LogFaulted(sequence.TenantId ?? SharedSequence, failures);
        await RecordAsync(sequence, exception.Message, stopping).ConfigureAwait(false);
    }

    /// <summary>Clears what a numbered sequence had failed: its count, and its fault, here and on its progress row.</summary>
    private async Task RecoverAsync(NumberedSequence sequence, CancellationToken stopping)
    {
        var key = new SequenceKey(sequence.TenantId);
        _failures.Remove(key);
        if (sequence.Numbering.Stopped is OrdinalSequencerFaultedException)
        {
            sequence.Numbering.Resume();
        }

        if (_faulted.Contains(key) && await RecordAsync(sequence, null, stopping).ConfigureAwait(false))
        {
            _faulted.Remove(key);
            LogRecovered(sequence.TenantId ?? SharedSequence);
        }
    }

    /// <summary>Writes a fault, or its end, to the progress row; a failure is logged and costs only the other instances' answer meanwhile.</summary>
    /// <returns>Whether it was written.</returns>
    private async Task<bool> RecordAsync(NumberedSequence sequence, string? fault, CancellationToken stopping)
    {
        try
        {
            await faults.WriteAsync(sequence.TenantId, fault, timeProvider.GetUtcNow(), stopping).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (!stopping.IsCancellationRequested)
        {
            LogFaultNotRecorded(exception, sequence.TenantId ?? SharedSequence);
            return false;
        }
    }

    /// <summary>How the log names the shared sequence, which has no tenant.</summary>
    private const string SharedSequence = "(shared)";

    [LoggerMessage(Level = LogLevel.Information, Message = "This instance ({Owner}) numbers the virtual streams.")]
    private partial void LogTookOver(string owner);

    [LoggerMessage(Level = LogLevel.Error, Message = "Numbering the virtual streams failed; retrying after the interval.")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Numbering the sequence of tenant {Tenant} failed, {Failures} times in a row; the other sequences go on, and it is tried again at the next pass.")]
    private partial void LogBatchFailed(Exception exception, string tenant, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "The ordinal sequencer is faulted for the sequence of tenant {Tenant} after {Failures} failures in a row: reads and subscriptions by ordinal in it are refused with SEQUENCER_FAULTED until a batch succeeds.")]
    private partial void LogFaulted(string tenant, int failures);

    [LoggerMessage(Level = LogLevel.Information, Message = "The ordinal sequencer numbers the sequence of tenant {Tenant} again; its fault is cleared.")]
    private partial void LogRecovered(string tenant);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The fault of the sequence of tenant {Tenant} could not be written to its progress row; the other instances learn of it at the next try.")]
    private partial void LogFaultNotRecorded(Exception exception, string tenant);
}
