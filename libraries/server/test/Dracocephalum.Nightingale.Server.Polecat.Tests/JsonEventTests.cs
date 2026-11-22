using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using JasperFx.Events;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The event the gateway hands the store, with no store in sight: the client's type name kept
/// against the store's own, and the metadata split into the two columns and the headers and put
/// back together. That the store keeps its side of this is the integration tests'.
/// </summary>
public sealed class JsonEventTests
{
    private static readonly Guid Id = Guid.Parse("0198f3a2-0000-7000-8000-000000000042");

    [Fact]
    public void From_ShouldKeepTheClientsTypeNameWhenTheStoreAssignsItsOwn()
    {
        // Arrange
        IEvent sut = JsonEvent.From(new EventData(Id, "order_placed", Encoding.UTF8.GetBytes("""{"total":12}""")));

        // Act: what the store does on every append.
        sut.EventTypeName = "json_element";

        // Assert
        sut.EventTypeName.ShouldBe("order_placed");
        sut.Id.ShouldBe(Id);
        ((JsonElement)sut.Data).GetProperty("total").GetInt32().ShouldBe(12);
    }

    [Fact]
    public void From_WhenTheEventHasNoJson_ShouldStoreTheEmptyObject()
    {
        // Arrange
        var binaryOnly = new EventData(Id, "order_placed", ReadOnlyMemory<byte>.Empty, BinaryData: new byte[] { 1, 2, 3 });

        // Act
        var sut = JsonEvent.From(binaryOnly);

        // Assert
        sut.Data.GetRawText().ShouldBe("{}");
        JsonEvent.EmptyObject.ToArray().ShouldBe("{}"u8.ToArray());
    }

    [Fact]
    public void From_ShouldMoveTheTwoReservedKeysToTheirOwnPlaceAndKeepTheRestAsHeaders()
    {
        // Arrange
        var metadata = new JsonObject
        {
            [MetadataKeys.CorrelationId] = "checkout-7",
            [MetadataKeys.CausationId] = "cart-closed-3",
            ["source"] = "web",
            ["attempt"] = 2,
        };

        // Act
        var sut = JsonEvent.From(new EventData(Id, "order_placed", Encoding.UTF8.GetBytes("{}"), metadata));

        // Assert
        sut.CorrelationId.ShouldBe("checkout-7");
        sut.CausationId.ShouldBe("cart-closed-3");
        sut.Headers.ShouldNotBeNull().Keys.ShouldBe(["source", "attempt"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void From_WhenThereIsNothingForTheHeaders_ShouldSetNone(bool reservedOnly)
    {
        // Arrange
        var metadata = reservedOnly ? new JsonObject { [MetadataKeys.CorrelationId] = "checkout-7" } : null;

        // Act
        var sut = JsonEvent.From(new EventData(Id, "order_placed", Encoding.UTF8.GetBytes("{}"), metadata));

        // Assert
        (sut.Headers?.Count ?? 0).ShouldBe(0);
        sut.CorrelationId.ShouldBe(reservedOnly ? "checkout-7" : null);
    }

    [Fact]
    public void GetMetadata_ShouldGiveBackTheMetadataTheEventWasProposedWith()
    {
        // Arrange
        var metadata = new JsonObject
        {
            [MetadataKeys.CorrelationId] = "checkout-7",
            [MetadataKeys.CausationId] = "cart-closed-3",
            ["source"] = "web",
            ["attempt"] = 2,
            ["tags"] = new JsonArray("a", "b"),
            ["note"] = null,
        };
        var stored = JsonEvent.From(new EventData(Id, "order_placed", Encoding.UTF8.GetBytes("{}"), metadata));

        // Act
        var read = JsonEvent.GetMetadata(stored);

        // Assert
        JsonNode.DeepEquals(read, metadata).ShouldBeTrue(read.ToJsonString());
    }

    [Fact]
    public void GetMetadata_WhenTheEventCarriesNone_ShouldGiveAnEmptyObject()
    {
        // Arrange
        var stored = JsonEvent.From(new EventData(Id, "order_placed", Encoding.UTF8.GetBytes("{}")));

        // Act
        var read = JsonEvent.GetMetadata(stored);

        // Assert
        read.Count.ShouldBe(0);
    }
}
