using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

/// <summary>
/// A filter crosses the wire whole, both ways, and a wire filter that says nothing, or two things,
/// is refused where the server checks it, with the reason.
/// </summary>
public sealed class FilterConversionsTests
{
    [Fact]
    public void ToWire_ThenToEventFilter_ShouldRoundTripEachKind()
    {
        // Arrange
        var byPrefix = EventFilter.StreamName("orders-", "invoices-");
        var byRegex = EventFilter.EventTypeMatches("^Order");

        // Act
        var prefixWire = byPrefix.ToWire(checkpointInterval: 32);
        var regexWire = byRegex.ToWire();
        var prefixBack = prefixWire.ToEventFilter();
        var regexBack = regexWire.ToEventFilter();

        // Assert
        prefixWire.KindCase.ShouldBe(Filter.KindOneofCase.StreamName);
        prefixWire.StreamName.Prefix.ShouldBe(["orders-", "invoices-"]);
        prefixWire.CheckpointInterval.ShouldBe(32u);
        regexWire.EventType.Regex.ShouldBe("^Order");
        regexWire.CheckpointInterval.ShouldBe(0u);
        prefixBack.Target.ShouldBe(EventFilterTarget.StreamName);
        prefixBack.Prefixes.ShouldBe(["orders-", "invoices-"]);
        regexBack.Target.ShouldBe(EventFilterTarget.EventType);
        regexBack.Regex.ShouldBe("^Order");
    }

    [Fact]
    public void ToEventFilter_WhenTheWireFilterSaysNothingOrTwoThings_ShouldRefuseWithTheReason()
    {
        // Arrange
        var neitherKind = new Filter();
        var neitherShape = new Filter { StreamName = new Expression() };
        var bothShapes = new Filter { EventType = new Expression { Prefix = { "Order" }, Regex = "^Order" } };
        var badRegex = new Filter { EventType = new Expression { Regex = "(" } };

        // Act & Assert
        Should.Throw<ArgumentException>(() => neitherKind.ToEventFilter()).Message.ShouldContain("what it looks at");
        Should.Throw<ArgumentException>(() => neitherShape.ToEventFilter()).Message.ShouldContain("not both and not neither");
        Should.Throw<ArgumentException>(() => bothShapes.ToEventFilter()).Message.ShouldContain("not both and not neither");
        Should.Throw<ArgumentException>(() => badRegex.ToEventFilter()).Message.ShouldContain("regular expression");
    }
}
