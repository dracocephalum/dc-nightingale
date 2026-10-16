namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>
/// A tail a test moves by hand. Waiters wake when <see cref="Advance"/> takes the head beyond what
/// they saw, or <see cref="Number"/> takes the numbering to what they need, exactly as the real
/// one does.
/// </summary>
internal sealed class FakeTail : IStoreTail
{
    private readonly Mark _head = new();
    private readonly Mark _numbering = new();

    public long Head => _head.Value;

    public long Numbered => _numbering.Value;

    public OrdinalSequencerException? NumberingStopped { get; set; }

    public ValueTask<long> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Head);

    public void Advance(long head) => _head.Set(head);

    public void Number(long through) => _numbering.Set(through);

    public ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken) => _head.WaitForAsync(beyond + 1, cancellationToken);

    public ValueTask<long> WaitForNumberedAsync(long through, CancellationToken cancellationToken) => NumberingStopped is { } stopped ? ValueTask.FromException<long>(stopped) : _numbering.WaitForAsync(through, cancellationToken);

    private sealed class Mark
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _value;

        public long Value => Volatile.Read(ref _value);

        public void Set(long value)
        {
            TaskCompletionSource moved;
            lock (_gate)
            {
                _value = value;
                moved = _moved;
                _moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            moved.SetResult();
        }

        public async ValueTask<long> WaitForAsync(long atLeast, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task moved;
                lock (_gate)
                {
                    if (_value >= atLeast)
                    {
                        return _value;
                    }

                    moved = _moved.Task;
                }

                await moved.WaitAsync(cancellationToken);
            }
        }
    }
}
