using System.Data;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// Integration tests against the local SQL Server: the store adapter's conversions, the retry rule,
/// and the two guards the design relies on, the sticky type name and the idempotent schema. Every
/// test uses its own stream name, so the shared database keeps them independent.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class PolecatStreamStoreTests(SqlServerTestDatabase database)
{
    [Fact]
    public async Task Append_WhenANameIsLongerThanTheStoreHolds_ShouldSayWhichAndStoreNothing()
    {
        // Arrange: the store's columns hold so many bytes of UTF-8, and the database measures.
        var sut = database.Store;
        var fits = "fits-" + Guid.NewGuid().ToString("N") + "-" + new string('订', 60);
        var tooLong = "long-" + Guid.NewGuid().ToString("N") + "-" + new string('a', 250);
        var tooManyBytes = "wide-" + Guid.NewGuid().ToString("N") + "-" + new string('订', 90);

        // Act
        var stored = await sut.AppendAsync(fits, StreamState.NoStream, [new EventData(Guid.NewGuid(), "order_placed", "{}"u8.ToArray(), [])], TestContext.Current.CancellationToken);
        var stream = await Should.ThrowAsync<ValueTooLongException>(() => sut.AppendAsync(tooLong, StreamState.NoStream, [new EventData(Guid.NewGuid(), "order_placed", "{}"u8.ToArray(), [])], TestContext.Current.CancellationToken));
        var bytes = await Should.ThrowAsync<ValueTooLongException>(() => sut.AppendAsync(tooManyBytes, StreamState.NoStream, [new EventData(Guid.NewGuid(), "order_placed", "{}"u8.ToArray(), [])], TestContext.Current.CancellationToken));
        var type = await Should.ThrowAsync<ValueTooLongException>(() => sut.AppendAsync("typed-" + Guid.NewGuid().ToString("N"), StreamState.NoStream, [new EventData(Guid.NewGuid(), new string('t', 600), "{}"u8.ToArray(), [])], TestContext.Current.CancellationToken));

        // Assert: a name of 60 characters in another script is 180 bytes and fits; 90 are 270 and
        // do not, though that is fewer characters than the 250 the column is declared with.
        stored.Revision.ShouldBe(0);
        stream.What.ShouldBe(ValueTooLongException.StreamName);
        bytes.What.ShouldBe(ValueTooLongException.StreamName);
        type.What.ShouldBe("event type");
        (await sut.ReadAsync(tooLong, Direction.Forwards, null, 1, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private static readonly byte[] PlacedBody = Encoding.UTF8.GetBytes("{\"orderId\":1,\"total\":42.5}");
    private static readonly byte[] PaidBody = Encoding.UTF8.GetBytes("{\"orderId\":1}");
    private static readonly byte[] Binary = [0x00, 0xFF, 0xFE, 0x80, 0x0A];

    [Fact]
    public async Task Append_ShouldTakeAsManyEventsAsTheBackendSaysItDoesAndRefuseOneMoreAsTooMany()
    {
        // Arrange: the store writes an append as one command, and the database takes 2100
        // parameters in one. How many events that is depends on the store's version; the
        // backend's number is measured, and this is what holds it to the truth.
        var sut = database.Store;
        var fits = NewStream();
        var tooMany = NewStream();

        // Act
        var stored = await sut.AppendAsync(fits, StreamState.NoStream, Many(PolecatStreamStore.MaxEventsPerAppend), TestContext.Current.CancellationToken);
        var refused = await Should.ThrowAsync<AppendSizeExceededException>(() => sut.AppendAsync(tooMany, StreamState.NoStream, Many(PolecatStreamStore.MaxEventsPerAppend + 1), TestContext.Current.CancellationToken));

        // Assert: all or none; the refused append left no stream behind.
        stored.Revision.ShouldBe(PolecatStreamStore.MaxEventsPerAppend - 1);
        refused.Stream.ShouldBe(tooMany);
        (await sut.ReadAsync(tooMany, Direction.Forwards, null, 1, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Append_WhenStreamIsNew_ShouldReturnLastRevisionAndAGlobalPosition()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;

        // Act
        var result = await sut.AppendAsync(stream, StreamState.NoStream, [Placed("order_placed"), Paid("order_paid")], TestContext.Current.CancellationToken);

        // Assert
        result.Revision.ShouldBe(1);
        result.Position.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Read_WhenForwardsFromStart_ShouldReturnTheClientsTypeNamesBodiesAndMetadata()
    {
        // Arrange: the type name is the client's, not the CLR type's; metadata round-trips including
        // the two keys the store keeps in their own columns.
        var stream = NewStream();
        var placed = Placed("order_placed", new JsonObject { ["$correlationId"] = "c-1", ["$causationId"] = "k-1", ["source"] = "test", ["attempt"] = 2 });
        var sut = database.Store;
        var appended = await sut.AppendAsync(stream, StreamState.NoStream, [placed, Paid("order_paid")], TestContext.Current.CancellationToken);

        // Act
        var slice = await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        var page = slice.ShouldNotBeNull();
        page.Head.ShouldBe(new StreamHead(0, 1));
        page.Events.Count.ShouldBe(2);
        page.Events[0].ShouldSatisfyAllConditions(
            record => record.Id.ShouldBe(placed.Id),
            record => record.Stream.ShouldBe(stream),
            record => record.Revision.ShouldBe(0),
            record => record.Type.ShouldBe("order_placed"),
            record => Encoding.UTF8.GetString(record.Data.Span).ShouldBe("{\"orderId\":1,\"total\":42.5}"),
            record => record.Metadata["$correlationId"].ShouldNotBeNull().GetValue<string>().ShouldBe("c-1"),
            record => record.Metadata["$causationId"].ShouldNotBeNull().GetValue<string>().ShouldBe("k-1"),
            record => record.Metadata["source"].ShouldNotBeNull().GetValue<string>().ShouldBe("test"),
            record => record.Metadata["attempt"].ShouldNotBeNull().GetValue<int>().ShouldBe(2));
        page.Events[1].ShouldSatisfyAllConditions(
            record => record.Revision.ShouldBe(1),
            record => record.Position.ShouldBe(appended.Position),
            record => record.Type.ShouldBe("order_paid"),
            record => record.Metadata.Count.ShouldBe(0));
    }

    [Fact]
    public async Task Read_ShouldReturnTheBodyExactlyAsTheClientSentIt()
    {
        // Arrange: the gateway holds no event classes, so the body must survive as JSON text - the
        // client's own casing, key order, nesting, number formatting and unicode included. Only
        // insignificant whitespace may differ, so the body is sent already minified.
        const string body = "{\"OrderId\":1,\"Total\":42.50,\"Lines\":[{\"Sku\":\"A-1\",\"Qty\":2}],\"Note\":\"Café ☕\",\"Nested\":{\"Deep\":{\"Flag\":true,\"Nothing\":null}}}";
        const string metadata = "{\"Source\":\"Checkout\",\"Attempt\":2,\"Weight\":0.50,\"Tags\":[\"Café\"]}";
        var stream = NewStream();
        var sut = database.Store;
        var proposed = new EventData(Guid.NewGuid(), "OrderPlaced", Encoding.UTF8.GetBytes(body), JsonNode.Parse(metadata).ShouldBeOfType<JsonObject>());
        await sut.AppendAsync(stream, StreamState.NoStream, [proposed], TestContext.Current.CancellationToken);

        // Act
        var slice = await sut.ReadAsync(stream, Direction.Forwards, null, 1, TestContext.Current.CancellationToken);

        // Assert
        var record = slice.ShouldNotBeNull().Events.ShouldHaveSingleItem();
        record.Type.ShouldBe("OrderPlaced");
        Encoding.UTF8.GetString(record.Data.Span).ShouldBe(body);
        record.Metadata.ToJsonString(NightingaleJson.Default).ShouldBe(metadata);
    }

    [Fact]
    public async Task Read_WhenBackwardsFromEnd_ShouldReturnNewestFirstAndHonourTheCount()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Placed("a"), Placed("b"), Placed("c")], TestContext.Current.CancellationToken);

        // Act
        var slice = await sut.ReadAsync(stream, Direction.Backwards, null, 2, TestContext.Current.CancellationToken);

        // Assert
        var page = slice.ShouldNotBeNull();
        page.Head.Last.ShouldBe(2);
        page.Events.Select(record => record.Revision).ShouldBe([2, 1]);
    }

    [Fact]
    public async Task Read_WhenForwardsFromAPosition_ShouldStartThereInclusive()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Placed("a"), Placed("b"), Placed("c")], TestContext.Current.CancellationToken);

        // Act
        var slice = await sut.ReadAsync(stream, Direction.Forwards, 1, 10, TestContext.Current.CancellationToken);

        // Assert
        slice.ShouldNotBeNull().Events.Select(record => record.Revision).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Read_WhenStreamDoesNotExist_ShouldReturnNull()
    {
        // Arrange
        var sut = database.Store;

        // Act
        var slice = await sut.ReadAsync(NewStream(), Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        slice.ShouldBeNull();
    }

    [Fact]
    public async Task Append_WhenExpectedRevisionIsStale_ShouldReportTheActualRevision()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Placed("a"), Placed("b")], TestContext.Current.CancellationToken);

        // Act
        var conflict = await Should.ThrowAsync<RevisionConflictException>(
            () => sut.AppendAsync(stream, StreamState.StreamRevision(0), [Placed("c")], TestContext.Current.CancellationToken));

        // Assert
        conflict.Stream.ShouldBe(stream);
        conflict.Expected.ShouldBe(StreamState.StreamRevision(0));
        conflict.ActualRevision.ShouldBe(1);
    }

    [Fact]
    public async Task Append_WhenNoStreamExpectedButItExists_ShouldConflict()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Placed("a")], TestContext.Current.CancellationToken);

        // Act
        var conflict = await Should.ThrowAsync<RevisionConflictException>(
            () => sut.AppendAsync(stream, StreamState.NoStream, [Placed("b")], TestContext.Current.CancellationToken));

        // Assert
        conflict.ActualRevision.ShouldBe(0);
    }

    [Fact]
    public async Task Append_WhenRetriedWithTheSameIds_ShouldSucceedWithTheOriginalResult()
    {
        // Arrange: the same batch, twice, as a client retrying after a lost response would send it.
        var stream = NewStream();
        var batch = new[] { Placed("a"), Placed("b") };
        var sut = database.Store;
        var first = await sut.AppendAsync(stream, StreamState.NoStream, batch, TestContext.Current.CancellationToken);

        // Act
        var retried = await sut.AppendAsync(stream, StreamState.NoStream, batch, TestContext.Current.CancellationToken);
        var slice = await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        retried.ShouldBe(first);
        slice.ShouldNotBeNull().Events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Append_WhenRetriedAtAnExplicitRevision_ShouldSucceedWithTheOriginalResult()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Placed("a")], TestContext.Current.CancellationToken);
        var batch = new[] { Placed("b"), Placed("c") };
        var first = await sut.AppendAsync(stream, StreamState.StreamRevision(0), batch, TestContext.Current.CancellationToken);

        // Act
        var retried = await sut.AppendAsync(stream, StreamState.StreamRevision(0), batch, TestContext.Current.CancellationToken);

        // Assert
        retried.ShouldBe(first);
        retried.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task Append_WhenAny_ShouldAppendAfterWhateverIsThere()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Placed("a")], TestContext.Current.CancellationToken);

        // Act
        var result = await sut.AppendAsync(stream, StreamState.Any, [Placed("b")], TestContext.Current.CancellationToken);

        // Assert
        result.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Schema_WhenAppliedAgain_ShouldHaveNoDelta()
    {
        // Arrange: the guard for the patched schema; a store release that stops honouring the
        // declared column or indexes shows up here as a pending migration.
        var store = database.Services.GetRequiredService<global::Polecat.IDocumentStore>();
        var databases = await store.Options.Tenancy!.BuildDatabasesAsync(TestContext.Current.CancellationToken);

        // Act & Assert
        await Should.NotThrowAsync(() => databases[0].AssertDatabaseMatchesConfigurationAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveChanges_ShouldRunATransactionParticipantAfterTheEventsItAppendsAndInTheirTransaction()
    {
        // Arrange: binary data is written beside the JSON by a participant in the append's own
        // transaction, which finds the rows the append inserted and refuses when it finds fewer.
        // The store promises a participant runs after all of its own statements and before the
        // commit; a release that ran it before would leave the first stream unwritten, and one
        // that committed apart would keep the second stream's event without its binary data. The
        // store's queued commands are not the seam: they bind every value as text, never bytes.
        var store = database.Services.GetRequiredService<global::Polecat.IDocumentStore>();
        var written = NewStream();
        var refused = NewStream();
        byte[] binary = [0x00, 0xFF, 0x10, 0x80];

        // Act
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(written, JsonEvent.From(Placed("order_placed")));
            session.AddTransactionParticipant(new BinaryDataSetter(written, binary));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(refused, JsonEvent.From(Placed("order_placed")));
            session.AddTransactionParticipant(new BinaryDataSetter(NewStream(), binary));
            await Should.ThrowAsync<SqlException>(() => session.SaveChangesAsync(TestContext.Current.CancellationToken));
        }

        // Assert: both columns on the one row, and nothing at all of the refused append.
        (await TestDatabases.ScalarAsync<byte[]>(database.Name, $"SELECT bdata FROM dbo.pc_events WHERE stream_id = '{written}'")).ShouldBe(binary);
        JsonNode.DeepEquals(
            JsonNode.Parse(await TestDatabases.ScalarAsync<string>(database.Name, $"SELECT CAST(data AS nvarchar(max)) FROM dbo.pc_events WHERE stream_id = '{written}'")),
            JsonNode.Parse(PlacedBody)).ShouldBeTrue();
        (await TestDatabases.ScalarAsync<int>(database.Name, $"SELECT COUNT(*) FROM dbo.pc_events WHERE stream_id = '{refused}'")).ShouldBe(0);
        (await TestDatabases.ScalarAsync<int>(database.Name, $"SELECT COUNT(*) FROM dbo.pc_streams WHERE id = '{refused}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Append_WithBinaryData_ShouldReadItBackBesideTheJsonByStreamCategoryAndAll()
    {
        // Arrange: both bodies, binary data alone, and JSON alone, in one append.
        var sut = database.Store;
        var category = "bin" + Guid.NewGuid().ToString("N")[..12];
        var stream = category + "-1";
        var both = Placed("person_registered", new JsonObject { [MetadataKeys.BinaryContentType] = "application/x-protobuf" }) with { BinaryData = Binary };
        var binaryOnly = new EventData(Guid.NewGuid(), "person_scanned", default, null, Binary);
        var jsonOnly = Paid("person_paid");

        // Act
        var appended = await sut.AppendAsync(stream, StreamState.NoStream, [both, binaryOnly, jsonOnly], TestContext.Current.CancellationToken);
        var byStream = (await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events;
        var byCategory = await sut.ReadVirtualAsync(new VirtualStreamName(VirtualStreamKind.Category, category), Direction.Forwards, 1, appended.Position, 10, TestContext.Current.CancellationToken);
        var byAll = (await sut.ReadAllAsync(Direction.Backwards, appended.Position, appended.Position, 100, TestContext.Current.CancellationToken)).Where(e => e.Stream == stream).Reverse().ToList();

        // Assert: every read carries the same three; the content type is an ordinary key on the way back.
        foreach (var events in new[] { byStream, byCategory, byAll })
        {
            events.Count.ShouldBe(3);
            events[0].BinaryData.ToArray().ShouldBe(Binary);
            JsonNode.DeepEquals(JsonNode.Parse(events[0].Data.Span), JsonNode.Parse(PlacedBody)).ShouldBeTrue();
            events[0].Metadata[MetadataKeys.BinaryContentType].ShouldNotBeNull().GetValue<string>().ShouldBe("application/x-protobuf");
            events[1].BinaryData.ToArray().ShouldBe(Binary);
            Encoding.UTF8.GetString(events[1].Data.Span).ShouldBe("{}");
            events[2].BinaryData.IsEmpty.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Append_WhenRetriedWithBinaryData_ShouldAnswerWithTheFirstAttemptAndWriteNothingMore()
    {
        // Arrange: the retry is recognised through the gateway's own read of the rows, since the
        // store's fetch refuses a row that has binary data and no serializer for its type.
        var sut = database.Store;
        var stream = NewStream();
        EventData[] events = [Placed("a") with { BinaryData = Binary }, new EventData(Guid.NewGuid(), "b", default, null, Binary)];
        var first = await sut.AppendAsync(stream, StreamState.NoStream, events, TestContext.Current.CancellationToken);

        // Act
        var retried = await sut.AppendAsync(stream, StreamState.NoStream, events, TestContext.Current.CancellationToken);

        // Assert
        retried.ShouldBe(first);
        (await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Append_WithBinaryDataOnEveryEventOfTheLargestAppend_ShouldStoreThemAll()
    {
        // Arrange: the binary data is written by a statement of its own, after the store's, so it
        // spends none of the parameters the store's limit on events is measured against.
        var sut = database.Store;
        var stream = NewStream();
        var events = Many(PolecatStreamStore.MaxEventsPerAppend).Select((e, i) => e with { BinaryData = BitConverter.GetBytes(i) }).ToList();

        // Act
        var stored = await sut.AppendAsync(stream, StreamState.NoStream, events, TestContext.Current.CancellationToken);
        var read = (await sut.ReadAsync(stream, Direction.Forwards, null, PolecatStreamStore.MaxEventsPerAppend, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events;

        // Assert: each event's own bytes on its own row.
        stored.Revision.ShouldBe(PolecatStreamStore.MaxEventsPerAppend - 1);
        read.Select(e => BitConverter.ToInt32(e.BinaryData.Span)).ShouldBe(Enumerable.Range(0, PolecatStreamStore.MaxEventsPerAppend));
    }

    /// <summary>Sets the binary data of a stream's first event, and refuses unless it finds that event.</summary>
    private sealed class BinaryDataSetter(string stream, byte[] binary) : global::Polecat.ITransactionParticipant
    {
        public async Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = new SqlCommand("UPDATE dbo.pc_events SET bdata = @bdata WHERE stream_id = @stream AND version = 1; IF @@ROWCOUNT <> 1 THROW 50000, 'The appended row was not found.', 1;", connection, transaction);
            command.Parameters.Add("@bdata", SqlDbType.VarBinary, -1).Value = binary;
            command.Parameters.Add("@stream", SqlDbType.VarChar, 250).Value = stream;
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static List<EventData> Many(int count) => Enumerable.Range(0, count).Select(_ => Paid("order_paid")).ToList();

    private static string NewStream() => "orders-" + Guid.NewGuid().ToString("N");

    private static EventData Placed(string type, JsonObject? metadata = null) => new(Guid.NewGuid(), type, PlacedBody, metadata);

    private static EventData Paid(string type) => new(Guid.NewGuid(), type, PaidBody);
}
