using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Examples.Polecat.CompetingConsumers;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class CompetingConsumersTests
{
    [Fact]
    public async Task RunAsync_ShouldPinStreamsEndBothOnUpdateAndHandOverWhatALeaverHeld()
    {
        // Arrange
        var master = ExampleEnvironment.ResolveMasterConnectionString();

        // Act
        var report = await Scenario.RunAsync(master, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: pinned, every stream's events went to one consumer, and all eight arrived.
        report.PinnedDelivered.ShouldBe(8);
        report.PinnedStreams[0].Intersect(report.PinnedStreams[1]).ShouldBeEmpty();
        report.PinnedStreams[0].Concat(report.PinnedStreams[1]).Order().ShouldBe(["orders-1", "orders-2", "orders-3", "orders-4"]);

        // The owner described both consumers, idle, each with its buffer.
        var live = report.Info.Live.ShouldNotBeNull();
        live.FromOwner.ShouldBeTrue();
        live.ConsumerCount.ShouldBe(2);
        live.Consumers.Count.ShouldBe(2);
        live.Consumers.ShouldAllBe(consumer => consumer.BufferSize == 10 && consumer.InFlightCount == 0);
        live.ConsumerBufferSize.ShouldBe(20);
        live.InFlightCount.ShouldBe(0);

        // The update ended both calls and changed the strategy, nothing else.
        report.EndedWith.ShouldBe([nameof(GroupUpdatedException), nameof(GroupUpdatedException)]);
        report.Updated.ConsumerStrategy.ShouldBe(ConsumerStrategy.RoundRobin);
        report.Updated.Start.ShouldBe(StreamPosition.Start);

        // Round robin: the slow consumer took revision 0 and held it; the quick one took 1 and 2;
        // when the slow one left, the quick one received revision 0 with no retry counted.
        report.QuickReceived.ShouldBe([(1, 0), (2, 0), (0, 0)]);
        report.HandedOver.ShouldBeTrue();
    }
}
