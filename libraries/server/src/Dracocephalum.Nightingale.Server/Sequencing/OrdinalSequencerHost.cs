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
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
/// <param name="leaseDuration">How long the lease lasts without renewal; <see cref="OrdinalSequencing.LeaseDuration"/> when not given. The renewal follows from it.</param>
internal sealed partial class OrdinalSequencerHost(
    ISubscriptionGroupStore leases,
    INumberedSequences sequences,
    OrdinalSequencingFollower follower,
    SubscriptionGroupRegistry registry,
    IOrdinalSequencer sequencer,
    TimeProvider timeProvider,
    ILogger<OrdinalSequencerHost> logger,
    TimeSpan? leaseDuration = null) : IHostedService, IDisposable
{
    private readonly TimeSpan _leaseDuration = leaseDuration ?? OrdinalSequencing.LeaseDuration;

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

        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "This instance ({Owner}) numbers the virtual streams.")]
    private partial void LogTookOver(string owner);

    [LoggerMessage(Level = LogLevel.Error, Message = "Numbering the virtual streams failed; retrying after the interval.")]
    private partial void LogFailed(Exception exception);
}
