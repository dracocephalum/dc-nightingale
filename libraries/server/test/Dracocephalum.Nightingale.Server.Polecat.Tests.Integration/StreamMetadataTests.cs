using System.Text;
using System.Text.Json.Nodes;

using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// A stream's metadata as <c>$metadata</c> events in its metadata stream, against the real store:
/// set before the stream exists, read back, raced on the metadata stream's revision, carried by
/// <c>$all</c> and <c>$mde</c> and left out of <c>$de</c>, kept by a delete and removed by a
/// tombstone.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class StreamMetadataTests(SqlServerTestDatabase database)
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");

    [Fact]
    public async Task Set_BeforeTheStreamExists_ShouldBeReadBackWithItsRevisionAndReplacedAsAWhole()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;

        // Act
        var first = await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 10, Custom = new JsonObject { ["owner"] = "billing" } }, TestContext.Current.CancellationToken);
        var second = await sut.SetStreamMetadataAsync(stream, StreamState.StreamRevision(first.Revision), new StreamMetadata { MaxAge = TimeSpan.FromDays(1), TempStream = true }, TestContext.Current.CancellationToken);
        var read = await sut.GetStreamMetadataAsync(stream, TestContext.Current.CancellationToken);
        var history = await sut.ReadAsync(StreamNames.ToMetadataStream(stream), Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert: the second set replaced the first; the stream itself still has no events.
        first.Revision.ShouldBe(0);
        second.Revision.ShouldBe(1);
        read.Revision.ShouldBe(1);
        read.Metadata.MaxAge.ShouldBe(TimeSpan.FromDays(1));
        read.Metadata.TempStream.ShouldBe(true);
        read.Metadata.MaxCount.ShouldBeNull();
        read.Metadata.Custom.ShouldBeNull();
        var events = history.ShouldNotBeNull().Events;
        events.Select(record => record.Type).ShouldBe([StreamNames.MetadataEventType, StreamNames.MetadataEventType]);
        Encoding.UTF8.GetString(events[0].Data.Span).ShouldBe("{\"$maxCount\":10,\"owner\":\"billing\"}");
        (await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Get_WhenNothingWasEverSet_ShouldAnswerRevisionMinusOneAndNoSettings()
    {
        // Arrange
        var stream = NewStream();
        await database.Store.AppendAsync(stream, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Act
        var read = await database.Store.GetStreamMetadataAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        read.Revision.ShouldBe(-1);
        read.Exists.ShouldBeFalse();
        read.Metadata.ShouldBe(StreamMetadata.Empty);
    }

    [Fact]
    public async Task Set_WhenTheMetadataStreamMovedOn_ShouldConflictOnTheMetadataStreamsRevision()
    {
        // Arrange: two instances that both expected no metadata; the store's unique index decides.
        var stream = NewStream();
        var sut = database.Store;
        await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 1 }, TestContext.Current.CancellationToken);

        // Act
        var conflict = await Should.ThrowAsync<RevisionConflictException>(() => sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 2 }, TestContext.Current.CancellationToken));

        // Assert
        conflict.Stream.ShouldBe(StreamNames.ToMetadataStream(stream));
        conflict.ActualRevision.ShouldBe(0);
        (await sut.GetStreamMetadataAsync(stream, TestContext.Current.CancellationToken)).Metadata.MaxCount.ShouldBe(1);
    }

    [Fact]
    public async Task Set_ShouldPutTheMetadataEventInAllAndTheMetadataEventsButNotTheDataEvents()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        var appended = await sut.AppendAsync(stream, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var set = await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 5 }, TestContext.Current.CancellationToken);
        var data = new VirtualStreamName(VirtualStreamKind.DataEvents, string.Empty);
        StreamNames.TryParseVirtual(StreamNames.MetadataEvents, out var metadata).ShouldBeTrue();

        // Act
        var all = await sut.ReadAllAsync(Direction.Forwards, appended.Position, set.Position, 10, TestContext.Current.CancellationToken);
        var dataEvents = await sut.ReadVirtualAsync(data, Direction.Forwards, appended.Position, set.Position, 10, TestContext.Current.CancellationToken);
        var metadataEvents = await sut.ReadVirtualAsync(metadata, Direction.Backwards, set.Position, set.Position, 1, TestContext.Current.CancellationToken);
        var dataHead = await sut.VirtualHeadAsync(data, set.Position, TestContext.Current.CancellationToken);

        // Assert: other tests' events may sit between the two positions, so only these two are looked for.
        all.Select(record => record.Position).ShouldContain(appended.Position);
        all.Select(record => record.Position).ShouldContain(set.Position);
        dataEvents.Select(record => record.Position).ShouldContain(appended.Position);
        dataEvents.ShouldAllBe(record => !record.Type.StartsWith('$'));
        metadataEvents.ShouldHaveSingleItem().Stream.ShouldBe(StreamNames.ToMetadataStream(stream));
        dataHead.ShouldNotBeNull().Last.ShouldBeLessThan(set.Position);
    }

    [Fact]
    public async Task Delete_ShouldLeaveTheMetadataAsItIs()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 3 }, TestContext.Current.CancellationToken);

        // Act
        await sut.DeleteAsync(stream, StreamState.Any, TestContext.Current.CancellationToken);

        // Assert
        (await sut.GetStreamMetadataAsync(stream, TestContext.Current.CancellationToken)).Metadata.MaxCount.ShouldBe(3);
    }

    [Fact]
    public async Task Tombstone_ShouldRemoveTheMetadataStreamWithTheStream()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 3 }, TestContext.Current.CancellationToken);

        // Act
        await sut.TombstoneAsync(stream, StreamState.Any, TestContext.Current.CancellationToken);

        // Assert: the name is free, and so is its metadata, which a stream taking it next starts without.
        (await sut.GetStreamMetadataAsync(stream, TestContext.Current.CancellationToken)).Revision.ShouldBe(-1);
        (await sut.ReadAsync(StreamNames.ToMetadataStream(stream), Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldBeNull();
        var again = await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 4 }, TestContext.Current.CancellationToken);
        again.Revision.ShouldBe(0);
    }

    private static string NewStream() => "meta" + Guid.NewGuid().ToString("N")[..8] + "-1";

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
