using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>The faults on the progress rows over an in-memory table: a fault is written beside the position and never over it, and cleared the same way.</summary>
public sealed class OrdinalSequencingFaultsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryContexts _contexts = new();
    private readonly OrdinalSequencingFaults _sut;

    public OrdinalSequencingFaultsTests() => _sut = new OrdinalSequencingFaults(_contexts);

    [Fact]
    public async Task WriteAsync_WhenTheSequenceWasNeverNumbered_ShouldMakeItsRowAtPositionZeroWithTheFault()
    {
        // Act
        await _sut.WriteAsync("7", "the batch timed out", At, TestContext.Current.CancellationToken);

        // Assert
        var row = (await ReadRowsAsync()).ShouldHaveSingleItem();
        (row.TenantId, row.Position, row.Fault, row.FaultedAt).ShouldBe(("7", 0, "the batch timed out", At));
    }

    [Fact]
    public async Task WriteAsync_WhenTheSequenceHasARow_ShouldWriteTheFaultAndLeaveThePosition()
    {
        // Arrange
        await _contexts.SeedAsync(new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = "7", Position = 40 });

        // Act
        await _sut.WriteAsync("7", "the batch timed out", At, TestContext.Current.CancellationToken);

        // Assert
        var row = (await ReadRowsAsync()).ShouldHaveSingleItem();
        (row.Position, row.Fault, row.FaultedAt).ShouldBe((40, "the batch timed out", At));
    }

    [Fact]
    public async Task WriteAsync_WhenTheFaultIsLong_ShouldKeepItsFirstThousandCharacters()
    {
        // Act
        await _sut.WriteAsync("7", new string('x', 1200), At, TestContext.Current.CancellationToken);

        // Assert
        (await ReadRowsAsync()).ShouldHaveSingleItem().Fault.ShouldBe(new string('x', 1000));
    }

    [Fact]
    public async Task WriteAsync_WhenTheFaultIsCleared_ShouldClearItsTimeAndLeaveThePosition()
    {
        // Arrange
        await _contexts.SeedAsync(new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = null, Position = 40, Fault = "the batch timed out", FaultedAt = At });

        // Act
        await _sut.WriteAsync(null, null, At.AddMinutes(1), TestContext.Current.CancellationToken);

        // Assert
        var row = (await ReadRowsAsync()).ShouldHaveSingleItem();
        (row.Position, row.Fault, row.FaultedAt).ShouldBe((40, null, null));
    }

    [Fact]
    public async Task WriteAsync_WhenThereIsNoFaultAndNoRow_ShouldWriteNothing()
    {
        // Act
        await _sut.WriteAsync("7", null, At, TestContext.Current.CancellationToken);

        // Assert
        (await ReadRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadFaultedAsync_ShouldNameOnlyTheSequencesThatCarryAFault()
    {
        // Arrange: the store's one sequence, which has no tenant, and two tenants' own.
        await _contexts.SeedAsync(
            new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = null, Position = 9, Fault = "the batch timed out", FaultedAt = At },
            new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = "7", Position = 40 },
            new OrdinalSequencingProgress { Id = Guid.NewGuid(), TenantId = "8", Position = 3, Fault = "a deadlock", FaultedAt = At });

        // Act
        var faulted = await _sut.ReadFaultedAsync(TestContext.Current.CancellationToken);

        // Assert
        faulted.ShouldBe([null, "8"], ignoreOrder: true);
    }

    private async Task<List<OrdinalSequencingProgress>> ReadRowsAsync()
    {
        await using var context = _contexts.CreateDbContext();
        return await context.OrdinalSequencingProgress.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }
}
