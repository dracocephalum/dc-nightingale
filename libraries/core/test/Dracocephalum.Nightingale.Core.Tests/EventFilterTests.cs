using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

/// <summary>
/// A filter keeps an event by its stream name or its type, by prefixes or a regular expression,
/// matches ordinally and without backtracking, and refuses what it cannot run before it is sent.
/// </summary>
public sealed class EventFilterTests
{
    [Fact]
    public void StreamFilterPrefix_ShouldKeepTheStreamsThatBeginWithAnyPrefixOrdinally()
    {
        // Arrange
        var sut = StreamFilter.Prefix("orders-", "invoices-");

        // Assert
        sut.Target.ShouldBe(EventFilterTarget.StreamName);
        sut.Prefixes.ShouldBe(["orders-", "invoices-"]);
        sut.Regex.ShouldBeNull();
        sut.Matches("orders-1", "anything").ShouldBeTrue();
        sut.Matches("invoices-7", "anything").ShouldBeTrue();
        sut.Matches("orders", "anything").ShouldBeFalse();
        sut.Matches("Orders-1", "anything").ShouldBeFalse();
        sut.Matches("shipments-1", "orders-1").ShouldBeFalse();
    }

    [Fact]
    public void EventTypeFilterPrefix_ShouldKeepTheTypesThatBeginWithAnyPrefix()
    {
        // Arrange
        var sut = EventTypeFilter.Prefix("Order");

        // Assert
        sut.Target.ShouldBe(EventFilterTarget.EventType);
        sut.Matches("anything", "OrderPlaced").ShouldBeTrue();
        sut.Matches("Order-1", "ShipmentSent").ShouldBeFalse();
    }

    [Fact]
    public void Matches_WithARegularExpression_ShouldMatchTheTargetWithoutBacktracking()
    {
        // Arrange
        var streams = StreamFilter.RegularExpression("^orders-[0-9]+$");
        var types = EventTypeFilter.RegularExpression("Placed$");

        // Assert
        streams.Regex.ShouldBe("^orders-[0-9]+$");
        streams.Prefixes.ShouldBeEmpty();
        streams.Matches("orders-42", "x").ShouldBeTrue();
        streams.Matches("orders-x", "x").ShouldBeFalse();
        types.Matches("x", "OrderPlaced").ShouldBeTrue();
        types.Matches("x", "OrderPaid").ShouldBeFalse();
    }

    [Theory]
    [InlineData("(")]
    [InlineData("(a)\\1")]
    [InlineData("a(?=b)")]
    public void RegularExpression_WhenTheExpressionCannotRunWithoutBacktracking_ShouldRefuseIt(string regex)
    {
        // Act
        var exception = Should.Throw<ArgumentException>(() => StreamFilter.RegularExpression(regex));

        // Assert
        exception.Message.ShouldContain("regular expression");
    }

    [Fact]
    public void Prefixes_ShouldBeAtLeastOneAndNoneEmpty()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => StreamFilter.Prefix());
        Should.Throw<ArgumentException>(() => EventTypeFilter.Prefix("Order", string.Empty));
        Should.Throw<ArgumentException>(() => EventTypeFilter.RegularExpression(string.Empty));
    }

    [Fact]
    public void ToString_ShouldSayWhatTheFilterKeeps()
    {
        // Act & Assert
        StreamFilter.Prefix("orders-", "invoices-").ToString().ShouldBe("StreamName begins with orders- | invoices-");
        EventTypeFilter.RegularExpression("^Order").ToString().ShouldBe("EventType matches /^Order/");
    }
}
