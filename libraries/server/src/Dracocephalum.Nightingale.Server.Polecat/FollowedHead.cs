namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// A head as it is followed: the highest mark published so far, the waits on it, and the failure
/// that ends every wait when whatever follows the store has stopped following. Every tail of this
/// backend publishes into one of these, the one tail of a shared sequence and each tenant's tail
/// under a sequence per tenant, so the waiting is written once.
/// </summary>
internal sealed class FollowedHead
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _head;
    private InvalidOperationException? _failure;

    /// <summary>Gets the head: the highest mark published.</summary>
    public long Head => Volatile.Read(ref _head);

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
