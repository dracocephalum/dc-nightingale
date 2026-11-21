using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class GroupSettingsTests
{
    public static TheoryData<GroupSettings, string> OutOfRange => new()
    {
        { GroupSettings.Default with { MessageTimeout = TimeSpan.Zero }, nameof(GroupSettings.MessageTimeout) },
        { GroupSettings.Default with { MaxRetryCount = -1 }, nameof(GroupSettings.MaxRetryCount) },
        { GroupSettings.Default with { CheckpointUpperBound = 0 }, nameof(GroupSettings.CheckpointUpperBound) },
        { GroupSettings.Default with { CheckpointAfter = TimeSpan.Zero }, nameof(GroupSettings.CheckpointAfter) },
        { GroupSettings.Default with { CheckpointLowerBound = 0 }, nameof(GroupSettings.CheckpointLowerBound) },
        { GroupSettings.Default with { BufferSize = 0 }, nameof(GroupSettings.BufferSize) },
        { GroupSettings.Default with { MaxSubscriberCount = -1 }, nameof(GroupSettings.MaxSubscriberCount) },
    };

    [Fact]
    public void Validate_WhenTheSettingsAreTheDefault_ShouldAcceptThem()
    {
        // Arrange
        var sut = GroupSettings.Default;

        // Act & Assert
        Should.NotThrow(sut.Validate);
    }

    [Fact]
    public void Validate_WhenNothingIsRetried_ShouldAcceptIt()
    {
        // Arrange
        var sut = GroupSettings.Default with { MaxRetryCount = 0 };

        // Act & Assert
        Should.NotThrow(sut.Validate);
    }

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void Validate_WhenASettingIsOutOfRange_ShouldRefuseItByName(GroupSettings sut, string setting)
    {
        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(sut.Validate).ParamName.ShouldBe(setting);
    }
}
