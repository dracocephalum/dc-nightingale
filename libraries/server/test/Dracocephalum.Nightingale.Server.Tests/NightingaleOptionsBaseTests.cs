using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>What each group of the host's settings accepts and what it refuses, with the message naming the setting.</summary>
public sealed class NightingaleOptionsBaseTests
{
    [Fact]
    public void ClusterValidate_WhenNoAddressIsAdvertised_ShouldPassAndGiveNoUri()
    {
        // Arrange
        var sut = new NightingaleOptionsBase.ClusterSettings();

        // Act & Assert
        Should.NotThrow(sut.Validate);
        sut.AdvertisedUri.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://node-1.example:5000")]
    [InlineData("https://node-1.example")]
    public void ClusterValidate_WhenTheAddressIsAnHttpUri_ShouldPassAndGiveIt(string address)
    {
        // Arrange
        var sut = new NightingaleOptionsBase.ClusterSettings { AdvertisedAddress = address };

        // Act & Assert
        Should.NotThrow(sut.Validate);
        sut.AdvertisedUri.ShouldBe(new Uri(address));
    }

    [Theory]
    [InlineData("/nightingale")]
    [InlineData("node-1.example")]
    [InlineData("ftp://node-1.example")]
    public void ClusterValidate_WhenTheAddressIsNotAnAbsoluteHttpUri_ShouldThrowNamingIt(string address)
    {
        // Arrange
        var sut = new NightingaleOptionsBase.ClusterSettings { AdvertisedAddress = address };

        // Act & Assert
        Should.Throw<InvalidOperationException>(sut.Validate).Message.ShouldContain(address);
    }

    [Fact]
    public void AuthValidate_WhenTheAdministratorHasNoPassword_ShouldThrowNamingTheWaysOut()
    {
        // Arrange: the defaults, which have authentication on.
        var sut = new NightingaleOptionsBase.AuthSettings();

        // Act
        var message = Should.Throw<InvalidOperationException>(sut.Validate).Message;

        // Assert
        message.ShouldContain("AdminPassword");
        message.ShouldContain("AdminDisabled");
        message.ShouldContain("Enabled");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AuthValidate_WhenThePasswordIsNotNeeded_ShouldPassWithoutOne(bool enabled, bool adminDisabled)
    {
        // Arrange
        var sut = new NightingaleOptionsBase.AuthSettings { Enabled = enabled, AdminDisabled = adminDisabled };

        // Act & Assert
        Should.NotThrow(sut.Validate);
    }

    [Fact]
    public void AuthValidate_WhenTheIterationsAreTooFew_ShouldThrowSayingHowManyThereWere()
    {
        // Arrange
        var sut = new NightingaleOptionsBase.AuthSettings { AdminPassword = "admin-password-12", Iterations = 9_999 };

        // Act & Assert
        Should.Throw<InvalidOperationException>(sut.Validate).Message.ShouldContain("9999");
    }

    [Theory]
    [InlineData(0, 5, 15, 5)]
    [InlineData(12, -1, 15, 5)]
    [InlineData(12, 5, -1, 5)]
    [InlineData(12, 5, 15, -1)]
    public void AuthValidate_WhenALimitIsOutOfRange_ShouldThrow(int minimumPasswordLength, int lockoutThreshold, int lockoutMinutes, int cacheMinutes)
    {
        // Arrange
        var sut = new NightingaleOptionsBase.AuthSettings
        {
            AdminPassword = "admin-password-12",
            MinimumPasswordLength = minimumPasswordLength,
            LockoutThreshold = lockoutThreshold,
            LockoutDuration = TimeSpan.FromMinutes(lockoutMinutes),
            VerificationCacheDuration = TimeSpan.FromMinutes(cacheMinutes),
        };

        // Act & Assert
        Should.Throw<InvalidOperationException>(sut.Validate).Message.ShouldContain("MinimumPasswordLength");
    }

    [Fact]
    public void AuthValidate_WhenLockoutIsOffAndNothingIsCached_ShouldPass()
    {
        // Arrange: zero is a setting for each of the three.
        var sut = new NightingaleOptionsBase.AuthSettings { AdminPassword = "admin-password-12", LockoutThreshold = 0, LockoutDuration = TimeSpan.Zero, VerificationCacheDuration = TimeSpan.Zero };

        // Act & Assert
        Should.NotThrow(sut.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void TenantsValidate_WhenTheIntervalIsNotPositive_ShouldThrowNamingIt(int seconds)
    {
        // Arrange
        var sut = new NightingaleOptionsBase.TenantSettings { RefreshInterval = TimeSpan.FromSeconds(seconds) };

        // Act & Assert
        Should.Throw<InvalidOperationException>(sut.Validate).Message.ShouldContain("RefreshInterval");
    }

    [Fact]
    public void TenantsValidate_WhenLeftAtTheDefault_ShouldPass()
    {
        // Arrange
        var sut = new NightingaleOptionsBase.TenantSettings();

        // Act & Assert
        Should.NotThrow(sut.Validate);
    }

    [Fact]
    public void DeletionValidate_WhenTheLevelIsNotOneOfThem_ShouldThrowListingThem()
    {
        // Arrange
        var sut = new NightingaleOptionsBase.DeletionSettings { Allowed = (DeletionLevel)99 };

        // Act
        var message = Should.Throw<InvalidOperationException>(sut.Validate).Message;

        // Assert
        message.ShouldContain("99");
        message.ShouldContain(nameof(DeletionLevel.Archive));
    }
}
