using System.Data;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Data.SqlClient;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// A stream's metadata as <c>$metadata</c> events in its metadata stream, against the real store:
/// set before the stream exists, read back, raced on the metadata stream's revision, carried by
/// <c>$all</c> and <c>$mde</c> and left out of <c>$de</c>, kept by a delete and removed by a
/// tombstone; and applied to a read of the stream, by the database, and to nothing else.
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

    [Fact]
    public async Task Read_WithMaxCountAndTruncateBefore_ShouldStartAtTheLaterBoundAndLeaveTheOtherReadsAlone()
    {
        // Arrange: six events; four kept by count would start at 2, the truncation at 3 is later.
        var stream = NewStream();
        var sut = database.Store;
        var appended = await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event(), Event(), Event(), Event(), Event()], TestContext.Current.CancellationToken);
        await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 4, TruncateBefore = 3 }, TestContext.Current.CancellationToken);
        var category = new VirtualStreamName(VirtualStreamKind.Category, stream[..stream.IndexOf('-', StringComparison.Ordinal)]);

        // Act
        var forwards = await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var backwards = await sut.ReadAsync(stream, Direction.Backwards, null, 10, TestContext.Current.CancellationToken);
        var eventual = await sut.ReadEventualAsync(stream, Direction.Forwards, 0, 1, TestContext.Current.CancellationToken);
        var stored = await sut.ReadAsStoredAsync(stream, 0, 10, TestContext.Current.CancellationToken);
        var virtualRead = await sut.ReadVirtualAsync(category, Direction.Forwards, 1, appended.Position, 10, TestContext.Current.CancellationToken);

        // Assert: metadata is the stream's reads' business, never a virtual stream's or the router's.
        forwards.ShouldNotBeNull().Head.ShouldBe(new StreamHead(3, 5));
        forwards.Events.Select(record => record.Revision).ShouldBe([3, 4, 5]);
        backwards.ShouldNotBeNull().Events.Select(record => record.Revision).ShouldBe([5, 4, 3]);
        eventual.ShouldNotBeNull().Events.ShouldHaveSingleItem().Revision.ShouldBe(3);
        stored.ShouldNotBeNull().Events.Count.ShouldBe(6);
        virtualRead.Count.ShouldBe(6);
    }

    [Fact]
    public async Task Read_WhenMetadataHidesEveryEvent_ShouldReadAnEmptyStreamThatExists()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { TruncateBefore = 100 }, TestContext.Current.CancellationToken);

        // Act
        var page = await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert: there and empty, as in the reference, never "no stream".
        page.ShouldNotBeNull().Head.ShouldBe(new StreamHead(100, 1));
        page.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Read_WithMaxAge_ShouldLeaveOutWhatTheDatabasesClockSaysIsOlder()
    {
        // Arrange: the first three events are made a day old; the store stamps the database's
        // clock, and that clock is what decides.
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event(), Event(), Event(), Event()], TestContext.Current.CancellationToken);
        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new SqlCommand("UPDATE pc_events SET timestamp = DATEADD(day, -1, timestamp) WHERE stream_id = @stream AND version <= 3", connection);
            command.Parameters.Add("@stream", SqlDbType.VarChar, 250).Value = stream;
            (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(3);
        }

        await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxAge = TimeSpan.FromHours(1) }, TestContext.Current.CancellationToken);

        // Act
        var forwards = await sut.ReadAsync(stream, Direction.Forwards, 0, 1, TestContext.Current.CancellationToken);
        var backwards = await sut.ReadAsync(stream, Direction.Backwards, null, 10, TestContext.Current.CancellationToken);

        // Assert: a page of one is the first young enough event, not an empty page.
        forwards.ShouldNotBeNull().Events.ShouldHaveSingleItem().Revision.ShouldBe(3);
        backwards.ShouldNotBeNull().Events.Select(record => record.Revision).ShouldBe([4, 3]);
    }

    private static string NewStream() => "meta" + Guid.NewGuid().ToString("N")[..8] + "-1";

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
