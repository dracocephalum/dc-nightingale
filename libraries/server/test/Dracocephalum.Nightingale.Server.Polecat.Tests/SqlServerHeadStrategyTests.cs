using Dracocephalum.Nightingale.Server.Polecat.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using FakeItEasy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The choice of strategy over horizons stood in: the horizon of each store partition when the
/// login may read it, else the transaction horizon of the database, else the best-effort strategy
/// with the store's own threshold.
/// </summary>
public sealed class SqlServerHeadStrategyTests
{
    private readonly ITransactionHorizon _partitions = A.Fake<ITransactionHorizon>();
    private readonly ITransactionHorizon _transactions = A.Fake<ITransactionHorizon>();
    private readonly IDbContextFactory<EventsDbContext> _events = new NoContexts();

    [Fact]
    public void DefaultThreshold_ShouldBeTheStoresOwn()
    {
        // Act & Assert: the best-effort strategy gives a gap up as the store's agent does, so a
        // version of the store that moves its default moves this one's, and this fails until it is followed.
        StaleThresholdHorizon.DefaultThreshold.ShouldBe(new global::Polecat.StoreOptions().DaemonSettings.StaleSequenceThreshold);
    }

    [Fact]
    public async Task Select_WhenThePartitionHorizonAnswers_ShouldChooseItWithoutAskingTheOther()
    {
        // Arrange
        A.CallTo(() => _partitions.CheckAvailabilityAsync(A<CancellationToken>._)).Returns(HorizonAvailability.Available);

        // Act
        var strategy = await SelectAsync();

        // Assert
        strategy.Kind.ShouldBe(HeadStrategyKind.PartitionHorizon);
        strategy.Horizon.ShouldBeSameAs(_partitions);
        A.CallTo(() => _transactions.CheckAvailabilityAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Select_WhenOnlyTheTransactionHorizonAnswers_ShouldChooseIt()
    {
        // Arrange
        A.CallTo(() => _partitions.CheckAvailabilityAsync(A<CancellationToken>._)).Returns(HorizonAvailability.Unavailable("No VIEW SERVER PERFORMANCE STATE."));
        A.CallTo(() => _transactions.CheckAvailabilityAsync(A<CancellationToken>._)).Returns(HorizonAvailability.Available);

        // Act
        var strategy = await SelectAsync();

        // Assert
        strategy.Kind.ShouldBe(HeadStrategyKind.TransactionHorizon);
        strategy.Horizon.ShouldBeSameAs(_transactions);
    }

    [Fact]
    public async Task Select_WhenNoHorizonAnswers_ShouldChooseTheBestEffortStrategyWithTheStoresThreshold()
    {
        // Arrange
        A.CallTo(() => _partitions.CheckAvailabilityAsync(A<CancellationToken>._)).Returns(HorizonAvailability.Unavailable("No VIEW SERVER PERFORMANCE STATE."));
        A.CallTo(() => _transactions.CheckAvailabilityAsync(A<CancellationToken>._)).Returns(HorizonAvailability.Unavailable("No VIEW DATABASE STATE."));

        // Act
        var strategy = await SelectAsync();

        // Assert
        strategy.Kind.ShouldBe(HeadStrategyKind.BestEffort);
        strategy.Horizon.ShouldBeOfType<StaleThresholdHorizon>().MinimumAge.ShouldBe(StaleThresholdHorizon.DefaultThreshold);
    }

    private ValueTask<HeadStrategy> SelectAsync() =>
        SqlServerHeadStrategy.SelectAsync(_partitions, _transactions, _events, NullLogger.Instance, TestContext.Current.CancellationToken);

    /// <summary>A factory the choice hands on and never calls.</summary>
    private sealed class NoContexts : IDbContextFactory<EventsDbContext>
    {
        public EventsDbContext CreateDbContext() => throw new InvalidOperationException("Choosing a strategy reads nothing.");
    }
}
