using Dracocephalum.Nightingale.Server.Polecat.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using FakeItEasy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The choice of strategy over a horizon stood in: the transaction horizon when the login may
/// read it, the best-effort strategy with the store's own threshold when it may not.
/// </summary>
public sealed class SqlServerHeadStrategyTests
{
    private readonly ITransactionHorizon _horizon = A.Fake<ITransactionHorizon>();
    private readonly IDbContextFactory<EventsDbContext> _events = new NoContexts();

    [Fact]
    public void DefaultThreshold_ShouldBeTheStoresOwn()
    {
        // Act & Assert: the best-effort strategy gives a gap up as the store's agent does, so a
        // version of the store that moves its default moves this one's, and this fails until it is followed.
        StaleThresholdHorizon.DefaultThreshold.ShouldBe(new global::Polecat.StoreOptions().DaemonSettings.StaleSequenceThreshold);
    }

    [Fact]
    public async Task Select_WhenTheHorizonAnswers_ShouldChooseTheTransactionHorizon()
    {
        // Arrange
        A.CallTo(() => _horizon.CheckAvailabilityAsync(A<CancellationToken>._)).Returns(HorizonAvailability.Available);

        // Act
        var strategy = await SqlServerHeadStrategy.SelectAsync(_horizon, _events, NullLogger.Instance, TestContext.Current.CancellationToken);

        // Assert
        strategy.Kind.ShouldBe(HeadStrategyKind.TransactionHorizon);
        strategy.Horizon.ShouldBeSameAs(_horizon);
    }

    [Fact]
    public async Task Select_WhenTheHorizonDoesNotAnswer_ShouldChooseTheBestEffortStrategyWithTheStoresThreshold()
    {
        // Arrange
        A.CallTo(() => _horizon.CheckAvailabilityAsync(A<CancellationToken>._)).Returns(HorizonAvailability.Unavailable("No VIEW DATABASE STATE."));

        // Act
        var strategy = await SqlServerHeadStrategy.SelectAsync(_horizon, _events, NullLogger.Instance, TestContext.Current.CancellationToken);

        // Assert
        strategy.Kind.ShouldBe(HeadStrategyKind.BestEffort);
        strategy.Horizon.ShouldBeOfType<StaleThresholdHorizon>().MinimumAge.ShouldBe(StaleThresholdHorizon.DefaultThreshold);
    }

    /// <summary>A factory the choice hands on and never calls.</summary>
    private sealed class NoContexts : IDbContextFactory<EventsDbContext>
    {
        public EventsDbContext CreateDbContext() => throw new InvalidOperationException("Choosing a strategy reads nothing.");
    }
}
