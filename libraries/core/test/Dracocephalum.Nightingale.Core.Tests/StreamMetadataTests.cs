using System.Text;
using System.Text.Json.Nodes;

using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class StreamMetadataTests
{
    [Fact]
    public void ToJsonBytes_ShouldWriteTheReferencesKeysBesideTheCustomOnes()
    {
        // Arrange
        var sut = new StreamMetadata
        {
            MaxAge = TimeSpan.FromHours(1),
            MaxCount = 10,
            TruncateBefore = 3,
            CacheControl = TimeSpan.FromSeconds(30),
            TempStream = false,
            Custom = new JsonObject { ["owner"] = "billing" },
        };

        // Act
        var json = Encoding.UTF8.GetString(sut.ToJsonBytes());

        // Assert
        json.ShouldBe("{\"$maxAge\":3600,\"$maxCount\":10,\"$tb\":3,\"$cacheControl\":30,\"$tmp\":false,\"owner\":\"billing\"}");
    }

    [Fact]
    public void ToJsonBytes_WhenNothingIsSet_ShouldWriteAnEmptyObject()
    {
        // Act & Assert
        Encoding.UTF8.GetString(StreamMetadata.Empty.ToJsonBytes()).ShouldBe("{}");
    }

    [Fact]
    public void FromJson_ShouldReadBackWhatToJsonBytesWrote()
    {
        // Arrange
        var written = new StreamMetadata
        {
            MaxAge = TimeSpan.FromDays(7),
            MaxCount = 1,
            TruncateBefore = 0,
            CacheControl = TimeSpan.FromMinutes(1),
            TempStream = true,
            Custom = new JsonObject { ["owner"] = "billing", ["tags"] = new JsonArray("a", "b") },
        };

        // Act
        var read = StreamMetadata.FromJson(written.ToJsonBytes());

        // Assert
        read.MaxAge.ShouldBe(TimeSpan.FromDays(7));
        read.MaxCount.ShouldBe(1);
        read.TruncateBefore.ShouldBe(0);
        read.CacheControl.ShouldBe(TimeSpan.FromMinutes(1));
        read.TempStream.ShouldBe(true);
        var custom = read.Custom.ShouldNotBeNull();
        custom["owner"].ShouldNotBeNull().GetValue<string>().ShouldBe("billing");
        custom["tags"].ShouldNotBeNull().AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void FromJson_WhenOnlyCustomKeysAreThere_ShouldLeaveEverySettingUnset()
    {
        // Act
        var read = StreamMetadata.FromJson("{\"owner\":\"billing\"}"u8);

        // Assert
        read.MaxAge.ShouldBeNull();
        read.MaxCount.ShouldBeNull();
        read.TruncateBefore.ShouldBeNull();
        read.Custom.ShouldNotBeNull().Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"$maxCount\":\"ten\"}")]
    [InlineData("{\"$tb\":1.5}")]
    [InlineData("{\"$tmp\":1}")]
    public void FromJson_WhenTheBodyIsNotMetadata_ShouldThrow(string json)
    {
        // Act & Assert
        Should.Throw<FormatException>(() => StreamMetadata.FromJson(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Validate_WhenAclIsAmongTheCustomKeys_ShouldRefuseItAsUnsupported()
    {
        // Arrange
        var sut = new StreamMetadata { Custom = new JsonObject { ["$acl"] = new JsonObject { ["$r"] = "$all" } } };

        // Act & Assert
        Should.Throw<ArgumentException>(sut.Validate).Message.ShouldContain("$acl is not supported");
    }

    [Theory]
    [InlineData("$maxAge")]
    [InlineData("$maxCount")]
    [InlineData("$tb")]
    [InlineData("$cacheControl")]
    [InlineData("$tmp")]
    public void Validate_WhenAReservedKeyIsAmongTheCustomKeys_ShouldRefuseIt(string key)
    {
        // Arrange
        var sut = new StreamMetadata { Custom = new JsonObject { [key] = 1 } };

        // Act & Assert
        Should.Throw<ArgumentException>(sut.Validate).Message.ShouldContain("set through its own property");
    }

    [Fact]
    public void Validate_WhenACustomKeyBeginsWithADollarButIsNotReserved_ShouldAcceptIt()
    {
        // Arrange: the reference keeps any other key as the application's.
        var sut = new StreamMetadata { Custom = new JsonObject { ["$owner"] = "billing" } };

        // Act & Assert
        Should.NotThrow(sut.Validate);
    }

    public static TheoryData<StreamMetadata> OutOfRange => new()
    {
        new StreamMetadata { MaxAge = TimeSpan.Zero },
        new StreamMetadata { MaxAge = TimeSpan.FromMilliseconds(1500) },
        new StreamMetadata { MaxCount = 0 },
        new StreamMetadata { TruncateBefore = -1 },
        new StreamMetadata { TruncateBefore = long.MaxValue },
        new StreamMetadata { CacheControl = TimeSpan.FromSeconds(-1) },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void Validate_WhenASettingIsOutOfRange_ShouldRefuseIt(StreamMetadata sut)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(sut.Validate);
    }

    [Theory]
    [InlineData(null, null, 9, 0)]
    [InlineData(3L, null, 9, 7)]
    [InlineData(20L, null, 9, 0)]
    [InlineData(null, 4L, 9, 4)]
    [InlineData(3L, 4L, 9, 7)]
    [InlineData(8L, 4L, 9, 4)]
    [InlineData(null, 15L, 9, 15)]
    [InlineData(1L, null, -1, 0)]
    public void GetFirstRevision_ShouldBeTheLaterOfTheCountsBoundAndTheTruncation(long? maxCount, long? truncateBefore, long last, long expected)
    {
        // Arrange
        var sut = new StreamMetadata { MaxCount = maxCount, TruncateBefore = truncateBefore };

        // Act & Assert: beyond the last revision when the truncation hides every event.
        sut.GetFirstRevision(last).ShouldBe(expected);
    }

    [Fact]
    public void CarriedOver_ShouldNameTheKeysStoredWithoutEffect()
    {
        // Act & Assert: taking one out of the list is what supporting it means.
        StreamMetadataKeys.CarriedOver.ShouldBe(["$cacheControl", "$tmp"]);
    }
}
