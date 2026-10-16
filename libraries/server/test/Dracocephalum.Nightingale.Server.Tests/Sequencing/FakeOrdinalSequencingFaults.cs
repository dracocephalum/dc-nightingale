using Dracocephalum.Nightingale.Server.Sequencing;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>The faults on the progress rows as a test keeps them: what is recorded, and every write in order.</summary>
internal sealed class FakeOrdinalSequencingFaults : IOrdinalSequencingFaults
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string?> _recorded = new(StringComparer.Ordinal);
    private readonly List<(string? TenantId, string? Fault)> _writes = [];

    public IReadOnlyList<(string? TenantId, string? Fault)> Writes
    {
        get
        {
            lock (_gate)
            {
                return [.. _writes];
            }
        }
    }

    public FakeOrdinalSequencingFaults Recording(string? tenantId, string fault)
    {
        lock (_gate)
        {
            _recorded[tenantId ?? string.Empty] = fault;
        }

        return this;
    }

    public Task<IReadOnlyCollection<string?>> ReadFaultedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<string?>>(_recorded.Where(pair => pair.Value is not null).Select(pair => pair.Key.Length == 0 ? null : pair.Key).ToList());
        }
    }

    public Task WriteAsync(string? tenantId, string? fault, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _recorded[tenantId ?? string.Empty] = fault;
            _writes.Add((tenantId, fault));
        }

        return Task.CompletedTask;
    }
}
