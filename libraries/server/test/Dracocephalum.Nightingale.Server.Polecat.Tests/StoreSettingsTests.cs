using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

public sealed class StoreSettingsTests
{
    [Fact]
    public void Validate_WhenPartitioningIsNotAKnownMode_ShouldRefuse()
    {
        // Arrange: a value the binder cannot have produced from a name, but a cast can.
        var sut = new NightingaleOptions.StoreSettings { Partitioning = (NightingaleOptions.StoreSettings.PartitioningMode)7 };

        // Act
        var exception = Should.Throw<InvalidOperationException>(sut.Validate);

        // Assert
        exception.Message.ShouldContain("None, Tenant, ArchivedStream");
    }

    [Fact]
    public void Validate_WhenOrdinalsMeetTenantPartitioning_ShouldRefuse()
    {
        // Arrange: ordinals number one global sequence; a sequence per tenant has none.
        var sut = new NightingaleOptions.StoreSettings { AssignOrdinals = true, Partitioning = NightingaleOptions.StoreSettings.PartitioningMode.Tenant };

        // Act
        var exception = Should.Throw<InvalidOperationException>(sut.Validate);

        // Assert
        exception.Message.ShouldContain("cannot be combined");
    }

    [Theory]
    [InlineData("Latin1_General_100_BIN2")]
    [InlineData("SQL_Latin1_General_CP1_CI_AS")]
    [InlineData(null)]
    public void Validate_WhenCollationIsAName_ShouldAccept(string? collation)
    {
        // Arrange
        var sut = new NightingaleOptions.StoreSettings { Collation = collation };

        // Act & Assert
        Should.NotThrow(sut.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Latin1 General")]
    [InlineData("x'; DROP DATABASE y; --")]
    public void Validate_WhenCollationIsNotAName_ShouldRefuse(string collation)
    {
        // Arrange
        var sut = new NightingaleOptions.StoreSettings { Collation = collation };

        // Act
        var exception = Should.Throw<InvalidOperationException>(sut.Validate);

        // Assert
        exception.Message.ShouldContain("not a collation name");
    }
}
