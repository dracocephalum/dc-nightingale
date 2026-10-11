using Shouldly;

namespace Dracocephalum.Nightingale.Core.Tests;

/// <summary>The reserved names and the category rule, which must say what the store's category column says.</summary>
public sealed class StreamNamesTests
{
    [Theory]
    [InlineData("orders-1", "orders")]
    [InlineData("orders-1-archive", "orders")]
    [InlineData("orders", "orders")]
    [InlineData("-orders", "")]
    public void CategoryOf_ShouldBeTheNameUpToTheFirstHyphenOrTheWholeName(string stream, string category)
    {
        // Act & Assert
        StreamNames.CategoryOf(stream).ShouldBe(category);
    }

    [Theory]
    [InlineData("$all", true)]
    [InlineData("$ce-orders", true)]
    [InlineData("orders-1", false)]
    public void IsReserved_ShouldBeTrueForNamesThatStartWithADollar(string stream, bool reserved)
    {
        // Act & Assert
        StreamNames.IsReserved(stream).ShouldBe(reserved);
    }
}
