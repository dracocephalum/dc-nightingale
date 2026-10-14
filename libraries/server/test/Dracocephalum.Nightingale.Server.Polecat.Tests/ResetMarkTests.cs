using System.Text;
using System.Text.Json.Nodes;

using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>How a reset is stored in a stream's metadata, and read back out of it.</summary>
public sealed class ResetMarkTests
{
    [Fact]
    public void ToBody_ShouldPutTheMarkInTheTruncationsPlaceAmongTheReferencesKeys()
    {
        // Arrange
        var metadata = new StreamMetadata { MaxAge = TimeSpan.FromSeconds(60), MaxCount = 5, TruncateBefore = 2, TempStream = true, Custom = new JsonObject { ["owner"] = "billing" } };

        // Act
        var body = Encoding.UTF8.GetString(ResetMark.ToBody(metadata, -6));

        // Assert: the reference's order, the given truncation in place of the metadata's own.
        body.ShouldBe("{\"$maxAge\":60,\"$maxCount\":5,\"$tb\":-6,\"$tmp\":true,\"owner\":\"billing\"}");
    }

    [Fact]
    public void ToBody_WithoutATruncation_ShouldLeaveTheKeyOut()
    {
        // Act
        var body = Encoding.UTF8.GetString(ResetMark.ToBody(new StreamMetadata { MaxCount = 5, TruncateBefore = 2 }, null));

        // Assert
        body.ShouldBe("{\"$maxCount\":5}");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(3L, null)]
    [InlineData(0L, null)]
    [InlineData(-6L, 6L)]
    public void Find_ShouldReadOnlyANegativeTruncationAsAReset(long? truncateBefore, long? continuesFrom)
    {
        // Act & Assert
        ResetMark.Find(new StreamMetadata { TruncateBefore = truncateBefore }).ShouldBe(continuesFrom);
    }
}
