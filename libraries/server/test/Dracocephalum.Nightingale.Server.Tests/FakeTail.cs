namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>
/// A tail a test moves by hand. Waiters wake when <see cref="Advance"/> takes the head beyond what
/// they saw, exactly as the real one does.
/// </summary>
internal sealed class FakeTail : IStoreTail
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _head;

    public long Head => Volatile.Read(ref _head);

    public ValueTask<long> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Head);

    public void Advance(long head)
    {
        TaskCompletionSource advanced;
        lock (_gate)
        {
            _head = head;
            advanced = _advanced;
            _advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        advanced.SetResult();
    }

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

            await advanced.WaitAsync(cancellationToken);
        }
    }
}
