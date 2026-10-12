namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>The sequencer's progress rows as a test keeps them: what each read answers, how many reads there were, and a failure to throw once.</summary>
internal sealed class FakeProgressReader : ISequencerProgressReader
{
    private readonly Dictionary<string, long> _rows = new(StringComparer.Ordinal);
    private Exception? _failOnce;
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public IReadOnlyCollection<string>? LastAskedFor { get; private set; }

    public FakeProgressReader Holding(string name, long position)
    {
        _rows[name] = position;
        return this;
    }

    public FakeProgressReader FailingOnce(Exception exception)
    {
        _failOnce = exception;
        return this;
    }

    public Task<IReadOnlyDictionary<string, long>> ReadAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        LastAskedFor = names;
        if (Interlocked.Exchange(ref _failOnce, null) is { } failure)
        {
            throw failure;
        }

        return Task.FromResult<IReadOnlyDictionary<string, long>>(names.Where(_rows.ContainsKey).ToDictionary(name => name, name => _rows[name], StringComparer.Ordinal));
    }
}
