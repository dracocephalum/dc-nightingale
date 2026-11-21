using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>The progress rows as the gateway's own context reads them, over an in-memory table.</summary>
public sealed class OrdinalSequencingProgressReaderTests
{
    private readonly InMemoryContexts _contexts = new();

    [Fact]
    public async Task ReadAsync_ShouldGiveTheRowsOfTheSequencesAskedAndNoOther()
    {
        // Arrange: the store's one sequence, which has no tenant, and two tenants' own.
        await _contexts.SeedAsync(
            new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = null, Position = 9 },
            new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = "7", Position = 40 },
            new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = "8", Position = 3 });
        var sut = new OrdinalSequencingProgressReader(_contexts);

        // Act
        var rows = await sut.ReadAsync([null, "8", "9"], TestContext.Current.CancellationToken);

        // Assert: a sequence never numbered has no row, and gets none here.
        rows.Select(row => (row.TenantId, row.Position)).ShouldBe([(null, 9L), ("8", 3L)], ignoreOrder: true);
    }
}
