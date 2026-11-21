using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class PersistentSubscriptionLiveInfoTests
{
    [Fact]
    public void Consumers_WhenNoneAreGiven_ShouldBeEmptyRatherThanNull()
    {
        // Act
        var sut = new PersistentSubscriptionLiveInfo(DateTimeOffset.UnixEpoch, 0, 0, 0);

        // Assert
        sut.Consumers.ShouldBeEmpty();
    }

    [Fact]
    public void Consumers_WhenGiven_ShouldBeThose()
    {
        // Arrange
        var consumer = new PersistentSubscriptionConsumerInfo(DateTimeOffset.UnixEpoch, null, 10, 0);

        // Act
        var sut = new PersistentSubscriptionLiveInfo(DateTimeOffset.UnixEpoch, 0, 0, 0, Consumers: [consumer]);

        // Assert
        sut.Consumers.ShouldBe([consumer]);
    }
}
