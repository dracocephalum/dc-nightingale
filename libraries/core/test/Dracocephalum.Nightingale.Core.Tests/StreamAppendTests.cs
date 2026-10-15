using System.Text;

using Shouldly;

namespace Dracocephalum.Nightingale.Core.Tests;

public sealed class StreamAppendTests
{
    [Fact]
    public void Written_ShouldBeTheStreamForEventsAndItsMetadataStreamForAMetadataSet()
    {
        // Arrange
        var events = new StreamAppend.OfEvents("orders-1", StreamState.Any, [new EventData(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{}"))]);
        var metadata = new StreamAppend.OfMetadata("orders-1", StreamState.NoStream, new StreamMetadata { MaxCount = 3 });

        // Act & Assert: the stream is the plain one either way; what is written differs.
        (events.Stream, events.Written).ShouldBe(("orders-1", "orders-1"));
        (metadata.Stream, metadata.Written).ShouldBe(("orders-1", "$$orders-1"));
    }

    [Fact]
    public void StreamConflictsException_ShouldNameEveryConflictInItsMessage()
    {
        // Act
        var exception = new StreamConflictsException([new StreamConflict(0, "orders-1", StreamState.StreamRevision(3), 5), new StreamConflict(1, "ledger-1", StreamState.NoStream, 0)]);

        // Assert
        exception.Message.ShouldContain("Nothing was written");
        exception.Message.ShouldContain("'orders-1'");
        exception.Message.ShouldContain("'ledger-1'");
        Should.Throw<ArgumentOutOfRangeException>(() => new StreamConflictsException([]));
    }
}
