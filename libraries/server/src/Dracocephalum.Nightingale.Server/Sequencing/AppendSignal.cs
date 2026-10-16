namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// What wakes the tails the moment this process appends, instead of leaving the append to be found
/// at the next poll. The stream stores pulse it after each append commits, and the
/// <see cref="SequenceTails"/> poller waits on it in place of its interval. A wait ends at a pulse
/// or when its time is up, whichever comes first, and a pulse nobody was waiting for ends the next
/// wait at once, so an append that commits while the poller is looking is never left to the
/// interval. One waiter at a time, the poller of the process; the sequencer and every subscription
/// wait on the tails, not on this. Only the appending instance is woken: another instance's poller
/// finds the event at its next look.
/// </summary>
/// <param name="timeProvider">The clock a wait's time runs on.</param>
public sealed class AppendSignal(TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _pulsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _pulses;
    private long _taken;

    /// <summary>Says an append has committed: the current wait ends, or the next one begins ended.</summary>
    public void Pulse()
    {
        TaskCompletionSource pulsed;
        lock (_gate)
        {
            _pulses++;
            pulsed = _pulsed;
            _pulsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        pulsed.TrySetResult();
    }

    /// <summary>Waits for a pulse, or for the time to run out, whichever comes first.</summary>
    /// <param name="timeout">How long to wait without a pulse: the poller's interval.</param>
    /// <param name="token">Cancelled when the poller stops.</param>
    /// <returns>A task that completes at a pulse or when the time is up, and is cancelled when the poller stops.</returns>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken token)
    {
        Task pulsed;
        lock (_gate)
        {
            if (_pulses > _taken)
            {
                _taken = _pulses;
                return;
            }

            pulsed = _pulsed.Task;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        var elapsed = Task.Delay(timeout, timeProvider, timer.Token);
        if (await Task.WhenAny(pulsed, elapsed).ConfigureAwait(false) == elapsed)
        {
            // Over, or cancelled: the delay says which. A pulse that landed at the same moment
            // stays untaken and ends the next wait.
            await elapsed.ConfigureAwait(false);
            return;
        }

        await timer.CancelAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _taken = _pulses;
        }
    }
}
