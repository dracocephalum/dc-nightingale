using Dracocephalum.Nightingale.Server.Data;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Data;

/// <summary>A group's filter round-trips through its column, and no filter is no value.</summary>
public sealed class FilterColumnTests
{
    [Fact]
    public void ToColumn_ThenFromColumn_ShouldRoundTripEachKindOfFilter()
    {
        // Arrange
        EventFilter[] filters =
        [
            StreamFilter.Prefix("orders-", "invoices-"),
            StreamFilter.RegularExpression("^orders-[0-9]+$"),
            EventTypeFilter.Prefix("Order"),
            EventTypeFilter.RegularExpression("Placed$"),
            StreamFilter.Category("orders", "invoices"),
            EventTypeFilter.Type("order_placed"),
        ];

        foreach (var filter in filters)
        {
            // Act
            var column = FilterColumn.ToColumn(filter);
            var back = FilterColumn.FromColumn(column);

            // Assert
            column.ShouldNotBeNull();
            back.ShouldNotBeNull();
            back.Target.ShouldBe(filter.Target);
            back.Prefixes.ShouldBe(filter.Prefixes);
            back.Regex.ShouldBe(filter.Regex);
            back.Exact.ShouldBe(filter.Exact);
        }
    }

    [Fact]
    public void ToColumn_ShouldWriteOneReadableObjectNamingTheTarget()
    {
        // Act
        var column = FilterColumn.ToColumn(StreamFilter.Prefix("orders-"));

        // Assert
        column.ShouldBe("{\"target\":\"StreamName\",\"prefixes\":[\"orders-\"]}");
    }

    [Fact]
    public void FromColumn_WhenThereIsNoValue_ShouldBeNoFilter()
    {
        // Act & Assert
        FilterColumn.ToColumn(null).ShouldBeNull();
        FilterColumn.FromColumn(null).ShouldBeNull();
    }
}
