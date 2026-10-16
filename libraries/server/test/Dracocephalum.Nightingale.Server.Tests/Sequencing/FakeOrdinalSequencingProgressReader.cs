using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Sequencing;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>The ordinal sequencer's progress rows as a test keeps them: what each read answers, how many reads there were, and a failure to throw once.</summary>
internal sealed class FakeOrdinalSequencingProgressReader : IOrdinalSequencingProgressReader
{
    private readonly List<OrdinalSequencingProgress> _rows = [];
    private Exception? _failOnce;
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public IReadOnlyCollection<string?>? LastAskedFor { get; private set; }

    public FakeOrdinalSequencingProgressReader Holding(string? tenantId, long position, string? fault = null)
    {
        _rows.RemoveAll(row => row.TenantId == tenantId);
        _rows.Add(new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = tenantId, Position = position, Fault = fault });
        return this;
    }

    public FakeOrdinalSequencingProgressReader FailingOnce(Exception exception)
    {
        _failOnce = exception;
        return this;
    }

    public Task<IReadOnlyList<OrdinalSequencingProgress>> ReadAsync(IReadOnlyCollection<string?> tenantIds, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        LastAskedFor = tenantIds;
        if (Interlocked.Exchange(ref _failOnce, null) is { } failure)
        {
            throw failure;
        }

        return Task.FromResult<IReadOnlyList<OrdinalSequencingProgress>>(_rows.Where(row => tenantIds.Contains(row.TenantId)).ToList());
    }
}
