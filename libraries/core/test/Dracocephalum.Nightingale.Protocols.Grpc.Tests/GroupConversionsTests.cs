using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class GroupConversionsTests
{
    private const string EventId = "7d2b6c1e-0000-4000-8000-000000000002";

    private static readonly DateTimeOffset Created = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GroupSettings_WhenTheGroupStartsAtTheEnd_ShouldSaySoOnTheWireAndBack()
    {
        // Arrange
        var settings = GroupSettings.Default with { Start = StreamPosition.End };

        // Act
        var wire = settings.ToWire();

        // Assert
        wire.StartCase.ShouldBe(Protocols.Grpc.V1.GroupSettings.StartOneofCase.FromEnd);
        wire.ToGroupSettings().Start.ShouldBe(StreamPosition.End);
    }

    [Fact]
    public void GroupSettings_WhenTheGroupStartsAtTheStart_ShouldSaySoOnTheWireAndBack()
    {
        // Arrange
        var settings = GroupSettings.Default with { Start = StreamPosition.Start };

        // Act
        var wire = settings.ToWire();

        // Assert
        wire.StartCase.ShouldBe(Protocols.Grpc.V1.GroupSettings.StartOneofCase.FromStart);
        wire.ToGroupSettings().Start.ShouldBe(StreamPosition.Start);
    }

    [Fact]
    public void GroupSettings_WhenTheGroupStartsAtAPosition_ShouldCarryThePositionOnTheWireAndBack()
    {
        // Arrange
        var settings = GroupSettings.Default with { Start = StreamPosition.From(41) };

        // Act
        var wire = settings.ToWire();

        // Assert
        wire.StartCase.ShouldBe(Protocols.Grpc.V1.GroupSettings.StartOneofCase.FromPosition);
        wire.FromPosition.ShouldBe(41);
        wire.ToGroupSettings().Start.ShouldBe(StreamPosition.From(41));
    }

    [Fact]
    public void ToGroupSettings_WhenNothingIsSet_ShouldBeTheDefault()
    {
        // Act
        var settings = new Protocols.Grpc.V1.GroupSettings().ToGroupSettings();

        // Assert
        settings.ShouldBe(GroupSettings.Default);
    }

    [Fact]
    public void GroupSettings_ShouldCarryEverySettingOnTheWireAndBack()
    {
        // Arrange
        var settings = new GroupSettings(
            StreamPosition.From(7),
            TimeSpan.FromSeconds(45),
            3,
            200,
            TimeSpan.FromSeconds(5),
            20,
            50,
            4,
            Numbering.Ordinal,
            ConsumerStrategy.Pinned);

        // Act
        var back = settings.ToWire().ToGroupSettings();

        // Assert
        back.ShouldBe(settings);
    }

    [Fact]
    public void ToPersistentSubscriptionInfo_WhenOnlyTheStoredFieldsAreSet_ShouldLeaveTheRestAbsent()
    {
        // Arrange
        var wire = new GroupInfo { Stream = "orders-1", Group = "billing", CreatedAt = Timestamp.FromDateTimeOffset(Created), ParkedCount = 2, OutboxCount = 3 };

        // Act
        var info = wire.ToPersistentSubscriptionInfo();

        // Assert
        info.ShouldBe(new PersistentSubscriptionInfo("orders-1", "billing", GroupSettings.Default, Created, null, 2, 3, false, null));
    }

    [Fact]
    public void ToPersistentSubscriptionInfo_WhenTheGroupRuns_ShouldCarryWhereAndHowItStands()
    {
        // Arrange
        var wire = new GroupInfo
        {
            Stream = "orders-1",
            Group = "billing",
            Settings = (GroupSettings.Default with { BufferSize = 50 }).ToWire(),
            Checkpoint = 12,
            Running = true,
            OwnerAddress = "http://node-2.example:2113",
            LastKnownPosition = 40,
            Live = new GroupLiveInfo
            {
                ConnectedAt = Timestamp.FromDateTimeOffset(Created),
                InFlightCount = 5,
                AwaitingRetryCount = 1,
                ConsumerBufferSize = 10,
                Checkpoint = 11,
                OldestInFlightAt = Timestamp.FromDateTimeOffset(Created.AddSeconds(1)),
                ConsumerAddress = "10.0.0.7:5000",
                AsOf = Timestamp.FromDateTimeOffset(Created.AddSeconds(2)),
                FromOwner = true,
                ConsumerCount = 2,
                Consumers =
                {
                    new ConsumerInfo { ConnectedAt = Timestamp.FromDateTimeOffset(Created), Address = "10.0.0.7:5000", BufferSize = 10, InFlightCount = 4 },
                    new ConsumerInfo { BufferSize = 10, InFlightCount = 1 },
                },
            },
        };

        // Act
        var info = wire.ToPersistentSubscriptionInfo();

        // Assert
        info.Settings.BufferSize.ShouldBe(50);
        info.Checkpoint.ShouldBe(12);
        info.Running.ShouldBeTrue();
        info.OwnerAddress.ShouldBe(new Uri("http://node-2.example:2113"));
        info.LastKnownPosition.ShouldBe(40);
        var live = info.Live.ShouldNotBeNull();
        live.ConnectedAt.ShouldBe(Created);
        live.InFlightCount.ShouldBe(5);
        live.AwaitingRetryCount.ShouldBe(1);
        live.ConsumerBufferSize.ShouldBe(10);
        live.Checkpoint.ShouldBe(11);
        live.OldestInFlightAt.ShouldBe(Created.AddSeconds(1));
        live.ConsumerAddress.ShouldBe("10.0.0.7:5000");
        live.AsOf.ShouldBe(Created.AddSeconds(2));
        live.FromOwner.ShouldBeTrue();
        live.ConsumerCount.ShouldBe(2);
        live.Consumers.ShouldBe(
        [
            new PersistentSubscriptionConsumerInfo(Created, "10.0.0.7:5000", 10, 4),
            new PersistentSubscriptionConsumerInfo(default, null, 10, 1),
        ]);
    }

    [Fact]
    public void ToPersistentSubscriptionInfo_WhenTheLiveNumbersHoldOnlyCounts_ShouldLeaveWhatIsAbsentNull()
    {
        // Arrange
        var wire = new GroupInfo { Stream = "orders-1", Group = "billing", Running = true, Live = new GroupLiveInfo { InFlightCount = 5 } };

        // Act
        var live = wire.ToPersistentSubscriptionInfo().Live.ShouldNotBeNull();

        // Assert
        live.Checkpoint.ShouldBeNull();
        live.OldestInFlightAt.ShouldBeNull();
        live.ConsumerAddress.ShouldBeNull();
        live.Consumers.ShouldBeEmpty();
    }

    [Fact]
    public void ToParkedMessageInfo_ShouldCarryEveryField()
    {
        // Arrange
        var wire = new ParkedMessage { Number = 3, EventId = EventId, Reason = "refused", RetryCount = 10, ParkedAt = Timestamp.FromDateTimeOffset(Created), Position = 40, Revision = 5 };

        // Act
        var info = wire.ToParkedMessageInfo();

        // Assert
        info.ShouldBe(new ParkedMessageInfo(3, Guid.Parse(EventId), "refused", 10, Created, 40, 5));
    }

    [Fact]
    public void ToOutboxMessageInfo_ShouldCarryEveryField()
    {
        // Arrange
        var wire = new OutboxMessage { Number = 3, EventId = EventId, Reason = "timed out", RetryCount = 2, DueAt = Timestamp.FromDateTimeOffset(Created), Position = 40, Revision = 5 };

        // Act
        var info = wire.ToOutboxMessageInfo();

        // Assert
        info.ShouldBe(new OutboxMessageInfo(3, Guid.Parse(EventId), "timed out", 2, Created, 40, 5));
    }
}
