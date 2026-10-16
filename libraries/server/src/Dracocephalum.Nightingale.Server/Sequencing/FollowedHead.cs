namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// A head as it is followed: the highest mark published so far, the waits on it, the failure that
/// ends every wait for good when whatever follows the store has stopped following, and a stop that
/// ends every wait only until it is lifted, while whatever moves the head is not moving it but may
/// again: the ordinal sequencer disabled or faulted. Every tail publishes into one of these, the
/// sequence head of a tail and how far the sequencer has numbered it, so the waiting is written
/// once for every backend.
/// </summary>
public sealed class FollowedHead
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _head;
    private InvalidOperationException? _failure;
    private InvalidOperationException? _stopped;

    /// <summary>Gets the head: the highest mark published.</summary>
    public long Head => Volatile.Read(ref _head);

    /// <summary>Gets why the head is stopped, until the stop is lifted; <see langword="null"/> while it moves.</summary>
    public InvalidOperationException? Stopped => Volatile.Read(ref _stopped);

    /// <summary>Waits until the head is beyond a position.</summary>
    /// <param name="beyond">The position the caller has already seen.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head, greater than <paramref name="beyond"/>.</returns>
    /// <exception cref="InvalidOperationException">The head will not advance again; the cause is the inner exception.</exception>
    public ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken) => WaitForReachAsync(beyond + 1, cancellationToken);

    /// <summary>Waits until the head is at a mark or beyond it.</summary>
    /// <param name="mark">The mark the caller needs reached.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head, at least <paramref name="mark"/>.</returns>
    /// <exception cref="InvalidOperationException">The head will not advance again; the cause is the inner exception.</exception>
    public async ValueTask<long> WaitForReachAsync(long mark, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task advanced;
            lock (_gate)
            {
                if (_head >= mark)
                {
                    return _head;
                }

                // A head that has stopped being followed never advances again. Whoever waits on
                // it is told so, now and from here on, rather than left waiting for good.
                if (_failure is not null)
                {
                    throw _failure;
                }

                // A head that is stopped tells its waiters why for as long as it is, and waits on
                // again once the stop is lifted.
                if (_stopped is not null)
                {
                    throw _stopped;
                }

                advanced = _advanced.Task;
            }

            await advanced.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Publishes a mark; one not beyond the head changes nothing.</summary>
    /// <param name="mark">The mark.</param>
    /// <returns>Whether the head moved.</returns>
    public bool Publish(long mark)
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
        return advanced is not null;
    }

    /// <summary>Ends every wait with the reason the head is stopped, now and until <see cref="Resume"/>; what is already known stays known, and a mark published meanwhile still moves it.</summary>
    /// <param name="reason">What every waiter is told meanwhile.</param>
    public void Stop(InvalidOperationException reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        TaskCompletionSource advanced;
        lock (_gate)
        {
            Volatile.Write(ref _stopped, reason);
            advanced = _advanced;
            _advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        advanced.TrySetException(reason);
        _ = advanced.Task.Exception;
    }

    /// <summary>Lifts a stop: waits begun from now on wait for the head again.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            Volatile.Write(ref _stopped, null);
        }
    }

    /// <summary>Ends every wait, now and from here on, with the failure; what is already known stays known.</summary>
    /// <param name="failure">What every waiter is told.</param>
    public void Fail(InvalidOperationException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        TaskCompletionSource advanced;
        lock (_gate)
        {
            _failure = failure;
            advanced = _advanced;
        }

        // Observed here, so that a head nobody happened to be waiting on leaves no unobserved fault.
        advanced.TrySetException(failure);
        _ = advanced.Task.Exception;
    }
}
