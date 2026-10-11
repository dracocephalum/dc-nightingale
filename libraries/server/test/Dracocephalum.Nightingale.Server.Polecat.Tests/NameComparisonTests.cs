using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The comparer built from what SQL Server says of a collation: exact for a binary one, the same
/// flags on .NET's engine for a linguistic one, and ordinal until the database has been asked.
/// </summary>
public sealed class NameComparisonTests
{
    private const int EnglishUs = 1033;

    /// <summary>What SQL Server reports for a collation that names neither kana nor width sensitivity: it ignores both.</summary>
    private const int IgnoresKanaAndWidth = 65536 + 131072;

    [Theory]
    [InlineData("Latin1_General_100_BIN2_UTF8", 0)]
    [InlineData("Latin1_General_BIN", 0)]
    [InlineData("Latin1_General_100_CI_AS_SC_UTF8", null)]
    public void ForCollation_WhenBinaryOrUndescribed_ShouldCompareCodePoints(string name, int? style)
    {
        // Act
        var comparer = NameComparison.ForCollation(new DatabaseCollation(name, 65001, style, EnglishUs));

        // Assert
        comparer.ShouldBeSameAs(StringComparer.Ordinal);
    }

    [Fact]
    public void ForCollation_WhenCaseInsensitiveAccentSensitive_ShouldIgnoreCaseOnly()
    {
        // Act
        var comparer = NameComparison.ForCollation(new DatabaseCollation("Latin1_General_100_CI_AS_SC_UTF8", 65001, 1 + IgnoresKanaAndWidth, EnglishUs));

        // Assert
        comparer.Equals("Orders", "orders").ShouldBeTrue();
        comparer.Equals("café", "cafe").ShouldBeFalse();
        comparer.GetHashCode("Orders").ShouldBe(comparer.GetHashCode("orders"));
    }

    [Fact]
    public void ForCollation_WhenCaseAndAccentInsensitive_ShouldIgnoreBoth()
    {
        // Act
        var comparer = NameComparison.ForCollation(new DatabaseCollation("Latin1_General_100_CI_AI_SC_UTF8", 65001, 3 + IgnoresKanaAndWidth, EnglishUs));

        // Assert
        comparer.Equals("Café", "cafe").ShouldBeTrue();
        comparer.Equals("orders", "order").ShouldBeFalse();
    }

    [Fact]
    public void ForCollation_WhenCaseSensitiveButWidthInsensitive_ShouldTellCaseApartAndNotWidth()
    {
        // Act: what SQL Server reports for a _CS_AS collation that names no width sensitivity.
        var comparer = NameComparison.ForCollation(new DatabaseCollation("Latin1_General_100_CS_AS_SC_UTF8", 65001, IgnoresKanaAndWidth, EnglishUs));

        // Assert: a full-width A is the same letter; a capital is not.
        comparer.Equals("Orders", "orders").ShouldBeFalse();
        comparer.Equals("Ａ", "A").ShouldBeTrue();
    }

    [Fact]
    public void ForCollation_WhenTheLocaleIsUnknown_ShouldStillBuildAComparer()
    {
        // Act
        var comparer = NameComparison.ForCollation(new DatabaseCollation("Something_CI_AS", 65001, 1, 999999));

        // Assert
        comparer.Equals("Orders", "orders").ShouldBeTrue();
    }

    [Fact]
    public void Comparer_BeforeAndAfterAdoptingACollation_ShouldBeOrdinalThenTheCollations()
    {
        // Arrange
        var sut = new NameComparison();
        var before = sut.Comparer;

        // Act
        sut.Adopt(new DatabaseCollation("Latin1_General_100_CI_AS_SC_UTF8", 65001, 1, EnglishUs));

        // Assert
        before.ShouldBeSameAs(StringComparer.Ordinal);
        sut.Comparer.Equals("Orders", "orders").ShouldBeTrue();
    }
}
