using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Google.Protobuf;
using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class WireConversionsTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ToProposedEvent_ShouldCarryMetadataAsTheObjectsJsonText()
    {
        // Arrange: the client's own casing, a trailing-zero decimal, nesting and unicode, which a
        // typed map would not carry unchanged.
        const string json = "{\"OrderId\":1,\"Total\":42.50,\"Note\":\"Café ☕\",\"Nested\":{\"Flag\":true}}";
        var metadata = JsonNode.Parse(json).ShouldBeOfType<JsonObject>();
        var eventData = new EventData(Guid.NewGuid(), "OrderPlaced", Encoding.UTF8.GetBytes("{}"), metadata);

        // Act
        var proposed = eventData.ToProposedEvent();

        // Assert
        proposed.Metadata.ToStringUtf8().ShouldBe(json);
    }

    [Fact]
    public void ToStreamMetadata_ShouldReadBackEverySettingAndTheCustomKeysToWireWrote()
    {
        // Arrange
        var metadata = new Dracocephalum.Nightingale.StreamMetadata
        {
            MaxAge = TimeSpan.FromHours(1),
            MaxCount = 0,
            TruncateBefore = 0,
            CacheControl = TimeSpan.FromSeconds(30),
            TempStream = false,
            Custom = new JsonObject { ["owner"] = "billing" },
        };

        // Act
        var wire = metadata.ToWire();
        var back = wire.ToStreamMetadata();

        // Assert: a zero and a false are settings, not absences; nothing is checked on the way.
        wire.HasMaxCount.ShouldBeTrue();
        wire.HasTempStream.ShouldBeTrue();
        back.MaxAge.ShouldBe(TimeSpan.FromHours(1));
        back.MaxCount.ShouldBe(0);
        back.TruncateBefore.ShouldBe(0);
        back.CacheControl.ShouldBe(TimeSpan.FromSeconds(30));
        back.TempStream.ShouldBe(false);
        back.Custom.ShouldNotBeNull()["owner"].ShouldNotBeNull().GetValue<string>().ShouldBe("billing");
    }

    [Fact]
    public void ToStreamMetadata_WhenNothingIsSet_ShouldLeaveEverySettingUnset()
    {
        // Act
        var back = new Protocols.Grpc.V1.StreamMetadata().ToStreamMetadata();
        var missing = ((Protocols.Grpc.V1.StreamMetadata?)null).ToStreamMetadata();

        // Assert
        back.ShouldBe(Dracocephalum.Nightingale.StreamMetadata.Empty);
        missing.ShouldBe(Dracocephalum.Nightingale.StreamMetadata.Empty);
    }

    [Fact]
    public void ToEventData_WhenMetadataIsEmpty_ShouldLeaveItNull()
    {
        // Arrange
        var proposed = new ProposedEvent { Id = Guid.NewGuid().ToString("D"), EventType = "x", Data = ByteString.CopyFromUtf8("{}") };

        // Act
        var eventData = proposed.ToEventData();

        // Assert
        eventData.Metadata.ShouldBeNull();
    }

    [Fact]
    public void ToEventData_WhenMetadataIsNotAnObject_ShouldThrow()
    {
        // Arrange
        var proposed = new ProposedEvent { Id = Guid.NewGuid().ToString("D"), EventType = "x", Data = ByteString.CopyFromUtf8("{}"), Metadata = ByteString.CopyFromUtf8("[1,2]") };

        // Act & Assert
        Should.Throw<JsonException>(() => proposed.ToEventData());
    }

    [Fact]
    public void RecordedEvent_ShouldCarryTheOrdinalOnlyWhenTheRecordHasOne()
    {
        // Arrange
        var numbered = new EventRecord(Guid.NewGuid(), "orders-1", 0, 5, "OrderPlaced", Created, Encoding.UTF8.GetBytes("{}"), [], 3);
        var plain = numbered with { Ordinal = null };

        // Act
        var wireNumbered = numbered.ToRecordedEvent();
        var wirePlain = plain.ToRecordedEvent();

        // Assert
        wireNumbered.HasOrdinal.ShouldBeTrue();
        wireNumbered.Ordinal.ShouldBe(3);
        wirePlain.HasOrdinal.ShouldBeFalse();
        wireNumbered.ToEventRecord().Ordinal.ShouldBe(3);
        wirePlain.ToEventRecord().Ordinal.ShouldBeNull();
    }

    [Theory]
    [InlineData(Numbering.Global, Protocols.Grpc.V1.Numbering.Global)]
    [InlineData(Numbering.Ordinal, Protocols.Grpc.V1.Numbering.Ordinal)]
    public void Numbering_ShouldRoundTripAndTreatUnspecifiedAsGlobal(Numbering numbering, Protocols.Grpc.V1.Numbering wire)
    {
        // Act & Assert
        numbering.ToWire().ShouldBe(wire);
        wire.ToNumbering().ShouldBe(numbering);
        Protocols.Grpc.V1.Numbering.Unspecified.ToNumbering().ShouldBe(Numbering.Global);
    }

    [Theory]
    [InlineData(ConsumerStrategy.RoundRobin, Protocols.Grpc.V1.ConsumerStrategy.RoundRobin)]
    [InlineData(ConsumerStrategy.Pinned, Protocols.Grpc.V1.ConsumerStrategy.Pinned)]
    [InlineData(ConsumerStrategy.DispatchToSingle, Protocols.Grpc.V1.ConsumerStrategy.DispatchToSingle)]
    [InlineData(ConsumerStrategy.PinnedByCorrelation, Protocols.Grpc.V1.ConsumerStrategy.PinnedByCorrelation)]
    public void ConsumerStrategy_ShouldRoundTripAndTreatUnspecifiedAsRoundRobin(ConsumerStrategy strategy, Protocols.Grpc.V1.ConsumerStrategy wire)
    {
        // Act & Assert
        strategy.ToWire().ShouldBe(wire);
        wire.ToConsumerStrategy().ShouldBe(strategy);
        Protocols.Grpc.V1.ConsumerStrategy.Unspecified.ToConsumerStrategy().ShouldBe(ConsumerStrategy.RoundRobin);
    }

    [Fact]
    public void RecordedEvent_ShouldRoundTripThroughTheDomainTypeUnchanged()
    {
        // Arrange
        const string metadata = "{\"$correlationId\":\"c-1\",\"Attempt\":2}";
        var recorded = new RecordedEvent
        {
            Id = Guid.NewGuid().ToString("D"),
            Stream = "orders-1",
            Revision = 3,
            Position = 40,
            EventType = "OrderPlaced",
            Created = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(Created),
            Data = ByteString.CopyFromUtf8("{\"OrderId\":1}"),
            Metadata = ByteString.CopyFromUtf8(metadata),
        };

        // Act
        var record = recorded.ToEventRecord();
        var back = record.ToRecordedEvent();

        // Assert
        record.Metadata["Attempt"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        back.ShouldBe(recorded);
    }

    [Fact]
    public void Events_ShouldCarryTheBinaryDataBesideTheJsonBothWays()
    {
        // Arrange: bytes that are no UTF-8 at all, so nothing on the way may treat them as text.
        byte[] binary = [0x00, 0xFF, 0xFE, 0x80, 0x0A];
        var proposed = new EventData(Guid.NewGuid(), "PersonRegistered", Encoding.UTF8.GetBytes("{\"Country\":\"NZ\"}"), new JsonObject { [MetadataKeys.BinaryContentType] = "application/x-protobuf" }, binary);
        var record = new EventRecord(Guid.NewGuid(), "people-1", 0, 7, "PersonRegistered", Created, Encoding.UTF8.GetBytes("{}"), [], BinaryData: binary);

        // Act
        var proposedBack = proposed.ToProposedEvent().ToEventData();
        var recordedWire = record.ToRecordedEvent();
        var recordBack = recordedWire.ToEventRecord();

        // Assert
        proposedBack.BinaryData.ToArray().ShouldBe(binary);
        proposedBack.Data.ToArray().ShouldBe(proposed.Data.ToArray());
        proposedBack.Metadata.ShouldNotBeNull()[MetadataKeys.BinaryContentType].ShouldNotBeNull().GetValue<string>().ShouldBe("application/x-protobuf");
        recordedWire.BinaryData.ToByteArray().ShouldBe(binary);
        recordBack.BinaryData.ToArray().ShouldBe(binary);
    }

    [Fact]
    public void Events_WithoutBinaryData_ShouldLeaveTheWireFieldEmpty()
    {
        // Arrange
        var proposed = new EventData(Guid.NewGuid(), "OrderPlaced", Encoding.UTF8.GetBytes("{}"));
        var record = new EventRecord(Guid.NewGuid(), "orders-1", 0, 5, "OrderPlaced", Created, Encoding.UTF8.GetBytes("{}"), []);

        // Act
        var proposedWire = proposed.ToProposedEvent();
        var recordedWire = record.ToRecordedEvent();

        // Assert: proto3 sends an empty bytes field as nothing at all, so a JSON event costs no byte more.
        proposedWire.BinaryData.IsEmpty.ShouldBeTrue();
        recordedWire.BinaryData.IsEmpty.ShouldBeTrue();
        proposedWire.ToEventData().BinaryData.IsEmpty.ShouldBeTrue();
        recordedWire.ToEventRecord().BinaryData.IsEmpty.ShouldBeTrue();
    }
}
