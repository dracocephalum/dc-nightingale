using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>
/// The sequences of a store that numbers each tenant on its own, and what stands in for the
/// sequencer when it is disabled, over tails that are never started: a sequence per tenant the
/// gateway keeps, those followed here, and a numbering stopped with its reason.
/// </summary>
public sealed class TenantSequencesTests : IAsyncDisposable
{
    private readonly InMemoryContexts _contexts = new();
    private readonly SequenceTails _tails;

    public TenantSequencesTests()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        _tails = new SequenceTails(
            _ => throw new InvalidOperationException("The tails are never started here, so no strategy is selected."),
            _contexts,
            new AppendSignal(time),
            time,
            NullLogger<SequenceTails>.Instance);
    }

    public ValueTask DisposeAsync() => _tails.DisposeAsync();

    [Fact]
    public async Task ListAsync_ShouldGiveASequencePerTenantEachWithItsOwnTail()
    {
        // Arrange
        await _contexts.SeedAsync(
            new Tenant { Id = Guid.NewGuid(), Name = "billing", StoreTenantId = "7" },
            new Tenant { Id = Guid.NewGuid(), Name = "shipping", StoreTenantId = "8" });
        var sut = new TenantSequences(_tails, _contexts);

        // Act
        var sequences = await sut.ListAsync(TestContext.Current.CancellationToken);

        // Assert: asking for a tenant's tail is what makes it followed.
        sequences.Select(sequence => sequence.TenantId).ShouldBe(["7", "8"], ignoreOrder: true);
        sequences.Select(sequence => sequence.Tail).Distinct().Count().ShouldBe(2);
        sequences.ShouldAllBe(sequence => ReferenceEquals(sequence.Tail, _tails.For(new SequenceKey(sequence.TenantId))));
        sequences.ShouldAllBe(sequence => ReferenceEquals(sequence.Numbering, _tails.NumberingOf(new SequenceKey(sequence.TenantId))));
    }

    [Fact]
    public void ListFollowed_ShouldGiveTheTenantsWhoseTailWasAskedForAndNotTheSharedSequence()
    {
        // Arrange
        _tails.For(new SequenceKey("7"));
        _tails.For(SequenceKey.Shared);
        var sut = new TenantSequences(_tails, _contexts);

        // Act
        var followed = sut.ListFollowed();

        // Assert
        followed.ShouldHaveSingleItem().TenantId.ShouldBe("7");
    }

    [Fact]
    public void Advances_ShouldBeTheTailsOwnCount()
    {
        // Arrange
        var sut = new TenantSequences(_tails, _contexts);

        // Act & Assert
        sut.Advances.ShouldBe(_tails.Advances);
    }

    [Fact]
    public async Task DisabledOrdinalSequencer_WhenStarted_ShouldStopTheNumberingOfEverySequenceFollowedNowOrLater()
    {
        // Arrange
        var before = _tails.For(new SequenceKey("7"));
        var sut = new DisabledOrdinalSequencer(_tails);

        // Act
        await sut.StartAsync(TestContext.Current.CancellationToken);
        var after = _tails.For(new SequenceKey("8"));
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert: a read by ordinal is refused with the reason, and not left waiting.
        before.NumberingStopped.ShouldBeOfType<OrdinalSequencerDisabledException>();
        after.NumberingStopped.ShouldBeOfType<OrdinalSequencerDisabledException>();
        await Should.ThrowAsync<OrdinalSequencerDisabledException>(async () => await after.WaitForNumberedAsync(1, TestContext.Current.CancellationToken));
    }
}
