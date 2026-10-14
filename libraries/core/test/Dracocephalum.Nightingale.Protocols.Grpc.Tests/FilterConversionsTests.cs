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
        var byPrefix = StreamFilter.Prefix("orders-", "invoices-");
        var byRegex = EventTypeFilter.RegularExpression("^Order");

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
    public void ToWire_ThenToEventFilter_ShouldRoundTripExactValuesOfEitherTarget()
    {
        // Arrange
        var byCategory = StreamFilter.Category("orders", "invoices");
        var byType = EventTypeFilter.Type("order_placed");

        // Act
        var categoryWire = byCategory.ToWire();
        var typeWire = byType.ToWire();

        // Assert
        categoryWire.StreamName.Exact.ShouldBe(["orders", "invoices"]);
        categoryWire.StreamName.Prefix.ShouldBeEmpty();
        typeWire.EventType.Exact.ShouldBe(["order_placed"]);
        categoryWire.ToEventFilter().Exact.ShouldBe(["orders", "invoices"]);
        typeWire.ToEventFilter().ShouldSatisfyAllConditions(
            back => back.Target.ShouldBe(EventFilterTarget.EventType),
            back => back.Exact.ShouldBe(["order_placed"]));
    }

    [Fact]
    public void GroupSettings_ToWireThenBack_ShouldCarryTheFilterAndNoneWhenThereIsNone()
    {
        // Arrange
        var filtered = GroupSettings.Default with { Filter = EventTypeFilter.Prefix("Order") };

        // Act
        var wire = filtered.ToWire();
        var back = wire.ToGroupSettings();
        var none = GroupSettings.Default.ToWire().ToGroupSettings();

        // Assert
        wire.Filter.EventType.Prefix.ShouldBe(["Order"]);
        back.Filter.ShouldNotBeNull().Target.ShouldBe(EventFilterTarget.EventType);
        back.Filter.Prefixes.ShouldBe(["Order"]);
        none.Filter.ShouldBeNull();
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
        Should.Throw<ArgumentException>(() => neitherShape.ToEventFilter()).Message.ShouldContain("one of the three");
        Should.Throw<ArgumentException>(() => bothShapes.ToEventFilter()).Message.ShouldContain("one of the three");
        Should.Throw<ArgumentException>(() => badRegex.ToEventFilter()).Message.ShouldContain("regular expression");
    }
}
