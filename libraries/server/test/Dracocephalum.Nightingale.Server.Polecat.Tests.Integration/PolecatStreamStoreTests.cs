using System.Text;
using System.Text.Json.Nodes;

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
    private static readonly byte[] PlacedBody = Encoding.UTF8.GetBytes("{\"orderId\":1,\"total\":42.5}");
    private static readonly byte[] PaidBody = Encoding.UTF8.GetBytes("{\"orderId\":1}");

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
        record.Metadata.ToJsonString(NightingaleJson.Options).ShouldBe(metadata);
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

    private static string NewStream() => "orders-" + Guid.NewGuid().ToString("N");

    private static EventData Placed(string type, JsonObject? metadata = null) => new(Guid.NewGuid(), type, PlacedBody, metadata);

    private static EventData Paid(string type) => new(Guid.NewGuid(), type, PaidBody);
}
