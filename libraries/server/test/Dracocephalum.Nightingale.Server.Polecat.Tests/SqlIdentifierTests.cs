using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

public sealed class SqlIdentifierTests
{
    [Theory]
    [InlineData("dbo", "[dbo]")]
    [InlineData("my schema", "[my schema]")]
    [InlineData("a]b", "[a]]b]")]
    [InlineData("x]; DROP TABLE pc_events; --", "[x]]; DROP TABLE pc_events; --]")]
    [InlineData("]]", "[]]]]]")]
    public void Quote_WhenGivenAName_ShouldBracketItAndDoubleEveryClosingBracket(string identifier, string expected)
    {
        var actual = SqlIdentifier.Quote(identifier);

        actual.ShouldBe(expected);
    }
}
