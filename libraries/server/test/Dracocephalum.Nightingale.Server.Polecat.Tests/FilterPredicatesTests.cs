using Dracocephalum.Nightingale.Server.Polecat.Data;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// A prefix or an exact filter as a predicate over the events table: a category and its hyphen is the
/// category column's equality less the bare name, anything else begins-with on the name or the
/// type, several prefixes are any of them, exact values are any of them on the category or the
/// type with nothing taken away, and a regular expression is not the database's.
/// </summary>
public sealed class FilterPredicatesTests
{
    [Theory]
    [InlineData("orders-", true, "orders")]
    [InlineData("orders", false, "")]
    [InlineData("orders-1", false, "")]
    [InlineData("order-s-", false, "")]
    [InlineData("-", false, "")]
    public void TryAsCategory_ShouldRecognizeACategoryAndItsHyphenOnly(string prefix, bool isCategory, string category)
    {
        // Act
        var result = FilterPredicates.TryAsCategory(prefix, out var found);

        // Assert
        result.ShouldBe(isCategory);
        found.ShouldBe(category);
    }

    [Fact]
    public void ToPredicate_WithACategoryPrefix_ShouldKeepTheCategoryLessTheBareName()
    {
        // Arrange
        var keep = FilterPredicates.ToPredicate(StreamFilter.Prefix("orders-")).Compile();

        // Assert
        keep(Row("orders-1", "orders", "x")).ShouldBeTrue();
        keep(Row("orders-", "orders", "x")).ShouldBeTrue();
        keep(Row("orders", "orders", "x")).ShouldBeFalse();
        keep(Row("ordersx-1", "ordersx", "x")).ShouldBeFalse();
    }

    [Fact]
    public void ToPredicate_WithOtherPrefixes_ShouldBeAnyOfThemOnTheNameOrTheType()
    {
        // Arrange
        var byName = FilterPredicates.ToPredicate(StreamFilter.Prefix("ord", "invoices-")).Compile();
        var byType = FilterPredicates.ToPredicate(EventTypeFilter.Prefix("Order")).Compile();

        // Assert
        byName(Row("orders-1", "orders", "x")).ShouldBeTrue();
        byName(Row("invoices-1", "invoices", "x")).ShouldBeTrue();
        byName(Row("shipments-1", "shipments", "x")).ShouldBeFalse();
        byType(Row("x-1", "x", "OrderPlaced")).ShouldBeTrue();
        byType(Row("x-1", "x", "ShipmentSent")).ShouldBeFalse();
    }

    [Fact]
    public void ToPredicate_WithCategoriesOrTypes_ShouldBeAnyOfThemExactlyAndKeepTheBareName()
    {
        // Arrange
        var byCategory = FilterPredicates.ToPredicate(StreamFilter.Category("orders", "invoices")).Compile();
        var byType = FilterPredicates.ToPredicate(EventTypeFilter.Type("order_placed")).Compile();

        // Assert: the stream named just the category is in it, unlike under the prefix.
        byCategory(Row("orders-1", "orders", "x")).ShouldBeTrue();
        byCategory(Row("orders", "orders", "x")).ShouldBeTrue();
        byCategory(Row("invoices-7", "invoices", "x")).ShouldBeTrue();
        byCategory(Row("ordersx-1", "ordersx", "x")).ShouldBeFalse();
        byType(Row("x-1", "x", "order_placed")).ShouldBeTrue();
        byType(Row("x-1", "x", "order_placed_v2")).ShouldBeFalse();
    }

    [Fact]
    public void ToPredicate_WithARegularExpression_ShouldRefuse()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => FilterPredicates.ToPredicate(StreamFilter.RegularExpression("^orders")));
    }

    private static EventRow Row(string stream, string category, string type) =>
        new() { StreamId = stream, Category = category, Type = type, Data = "{}", TenantId = "t" };
}
