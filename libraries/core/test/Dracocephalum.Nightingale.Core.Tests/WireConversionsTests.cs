using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
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
    [InlineData(Numbering.Global, Protocol.V1.Numbering.Global)]
    [InlineData(Numbering.Ordinal, Protocol.V1.Numbering.Ordinal)]
    public void Numbering_ShouldRoundTripAndTreatUnspecifiedAsGlobal(Numbering numbering, Protocol.V1.Numbering wire)
    {
        // Act & Assert
        numbering.ToWire().ShouldBe(wire);
        wire.ToNumbering().ShouldBe(numbering);
        Protocol.V1.Numbering.Unspecified.ToNumbering().ShouldBe(Numbering.Global);
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
}
