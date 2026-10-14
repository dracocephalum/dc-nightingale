using Shouldly;

namespace Dracocephalum.Nightingale.Core.Tests;

/// <summary>The reserved names, the metadata streams, and the category rule, which must say what the store's category column says.</summary>
public sealed class StreamNamesTests
{
    [Fact]
    public void ToMetadataStream_ShouldPrefixThePlainStreamsName()
    {
        // Act & Assert
        StreamNames.ToMetadataStream("orders-1").ShouldBe("$$orders-1");
    }

    [Theory]
    [InlineData("$all")]
    [InlineData("$$orders-1")]
    public void ToMetadataStream_WhenTheNameIsReserved_ShouldThrow(string stream)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => StreamNames.ToMetadataStream(stream));
    }

    [Theory]
    [InlineData("$$orders-1", true, "orders-1")]
    [InlineData("$$", false, "")]
    [InlineData("$$$all", false, "")]
    [InlineData("orders-1", false, "")]
    public void TryParseMetadataStream_ShouldRecogniseOnlyAPlainStreamsMetadataStream(string stream, bool expected, string original)
    {
        // Act
        var parsed = StreamNames.TryParseMetadataStream(stream, out var found);

        // Assert
        parsed.ShouldBe(expected);
        found.ShouldBe(original);
    }

    [Fact]
    public void TryParseVirtual_WhenTheDataEvents_ShouldGiveTheKindWithoutAKeyOrOrdinals()
    {
        // Act
        StreamNames.TryParseVirtual("$de", out var parsed).ShouldBeTrue();

        // Assert
        parsed.Kind.ShouldBe(VirtualStreamKind.DataEvents);
        parsed.Key.ShouldBeEmpty();
        parsed.Name.ShouldBe("$de");
        parsed.HasOrdinals.ShouldBeFalse();
    }

    [Theory]
    [InlineData("$mde")]
    [InlineData("$et-$metadata")]
    public void TryParseVirtual_WhenTheMetadataEvents_ShouldGiveTheEventTypeStreamOfMetadataEitherWay(string stream)
    {
        // Act
        StreamNames.TryParseVirtual(stream, out var parsed).ShouldBeTrue();

        // Assert: one name to follow and match, whichever a caller used.
        parsed.ShouldBe(new VirtualStreamName(VirtualStreamKind.EventType, "$metadata"));
        parsed.Name.ShouldBe("$et-$metadata");
        parsed.HasOrdinals.ShouldBeTrue();
    }

    [Theory]
    [InlineData("$metadata", true)]
    [InlineData("order_placed", false)]
    [InlineData("", false)]
    public void IsSystemEventType_ShouldBeTrueForATypeThatBeginsWithADollar(string type, bool expected)
    {
        // Act & Assert
        StreamNames.IsSystemEventType(type).ShouldBe(expected);
    }

    [Theory]
    [InlineData("orders-1", "orders")]
    [InlineData("orders-1-archive", "orders")]
    [InlineData("orders", "orders")]
    [InlineData("-orders", "")]
    public void CategoryOf_ShouldBeTheNameUpToTheFirstHyphenOrTheWholeName(string stream, string category)
    {
        // Act & Assert
        StreamNames.CategoryOf(stream).ShouldBe(category);
    }

    [Theory]
    [InlineData("$all", true)]
    [InlineData("$ce-orders", true)]
    [InlineData("orders-1", false)]
    public void IsReserved_ShouldBeTrueForNamesThatStartWithADollar(string stream, bool reserved)
    {
        // Act & Assert
        StreamNames.IsReserved(stream).ShouldBe(reserved);
    }
}
