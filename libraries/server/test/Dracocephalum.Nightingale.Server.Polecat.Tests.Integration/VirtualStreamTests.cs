using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The virtual streams read straight from the events table, through the persisted category column
/// and the two filtered indexes. The shared database holds other tests' events, so every category
/// and event type here carries a suffix of its own, and the guard the design relies on is asserted
/// here: a virtual stream's head is its own last event, never the global head.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class VirtualStreamTests(SqlServerTestDatabase database)
{
    [Fact]
    public async Task Head_ShouldBeTheStreamsOwnLastEventAndNeverTheGlobalHead()
    {
        // Arrange: two events in the category, then one somewhere else that moves the global head on.
        var (orders, _, placed) = Names();
        var sut = database.Store;
        var first = await sut.AppendAsync(orders + "-1", StreamState.NoStream, [Event(placed)], TestContext.Current.CancellationToken);
        var last = await sut.AppendAsync(orders + "-2", StreamState.NoStream, [Event(placed)], TestContext.Current.CancellationToken);
        var elsewhere = await sut.AppendAsync("other-" + Guid.NewGuid().ToString("N"), StreamState.NoStream, [Event("unrelated")], TestContext.Current.CancellationToken);
        var head = await ReachAsync(elsewhere.Position);

        // Act
        var bounds = await sut.VirtualHeadAsync(new VirtualStreamName(VirtualStreamKind.Category, orders), head, TestContext.Current.CancellationToken);

        // Assert
        bounds.ShouldBe(new StreamHead(first.Position, last.Position));
        bounds!.Last.ShouldBeLessThan(head);
    }

    [Fact]
    public async Task Read_ByCategoryAndByType_ShouldPageInPositionOrderEitherWay()
    {
        // Arrange
        var (orders, shipments, placed) = Names();
        var sut = database.Store;
        var a = await sut.AppendAsync(orders + "-1", StreamState.NoStream, [Event(placed), Event("paid_" + placed)], TestContext.Current.CancellationToken);
        var b = await sut.AppendAsync(orders + "-2", StreamState.NoStream, [Event(placed)], TestContext.Current.CancellationToken);
        var c = await sut.AppendAsync(shipments + "-1", StreamState.NoStream, [Event(placed)], TestContext.Current.CancellationToken);
        var head = await ReachAsync(c.Position);
        var category = new VirtualStreamName(VirtualStreamKind.Category, orders);
        var type = new VirtualStreamName(VirtualStreamKind.EventType, placed);

        // Act
        var byCategory = await sut.ReadVirtualAsync(category, Direction.Forwards, 0, head, 10, TestContext.Current.CancellationToken);
        var firstTwo = await sut.ReadVirtualAsync(category, Direction.Forwards, 0, head, 2, TestContext.Current.CancellationToken);
        var backwards = await sut.ReadVirtualAsync(category, Direction.Backwards, head, head, 10, TestContext.Current.CancellationToken);
        var byType = await sut.ReadVirtualAsync(type, Direction.Forwards, 0, head, 10, TestContext.Current.CancellationToken);
        var count = await sut.CountVirtualAsync(category, a.Position - 1, head, TestContext.Current.CancellationToken);

        // Assert
        byCategory.Select(record => record.Stream).ShouldBe([orders + "-1", orders + "-1", orders + "-2"]);
        byCategory.Select(record => record.Position).ShouldBe([a.Position - 1, a.Position, b.Position]);
        firstTwo.Count.ShouldBe(2);
        backwards.Select(record => record.Position).ShouldBe([b.Position, a.Position, a.Position - 1]);
        byType.Select(record => record.Stream).ShouldBe([orders + "-1", orders + "-2", shipments + "-1"]);
        count.ShouldBe(2);
    }

    [Fact]
    public async Task Read_ShouldReturnTheSameRecordAsTheStreamReadDoes()
    {
        // Arrange: the raw path and the store's path hydrate independently; they must agree byte for byte.
        var (orders, _, placed) = Names();
        var sut = database.Store;
        const string body = "{\"OrderId\":1,\"Total\":42.50,\"Note\":\"Café ☕\"}";
        var metadata = JsonNode.Parse("{\"Source\":\"Checkout\",\"$correlationId\":\"c-1\",\"Tags\":[\"Café\"]}").ShouldBeOfType<JsonObject>();
        var proposed = new EventData(Guid.NewGuid(), placed, Encoding.UTF8.GetBytes(body), metadata);
        var appended = await sut.AppendAsync(orders + "-1", StreamState.NoStream, [proposed], TestContext.Current.CancellationToken);
        var head = await ReachAsync(appended.Position);

        // Act
        var viaStream = (await sut.ReadAsync(orders + "-1", Direction.Forwards, null, 1, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events.ShouldHaveSingleItem();
        var viaType = (await sut.ReadVirtualAsync(new VirtualStreamName(VirtualStreamKind.EventType, placed), Direction.Forwards, 0, head, 1, TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        // Assert
        viaType.ShouldSatisfyAllConditions(
            record => record.Id.ShouldBe(viaStream.Id),
            record => record.Stream.ShouldBe(viaStream.Stream),
            record => record.Revision.ShouldBe(viaStream.Revision),
            record => record.Position.ShouldBe(viaStream.Position),
            record => record.Type.ShouldBe(viaStream.Type),
            record => record.Created.ShouldBe(viaStream.Created),
            record => Encoding.UTF8.GetString(record.Data.Span).ShouldBe(body),
            record => record.Metadata.ToJsonString(NightingaleJson.Options).ShouldBe(viaStream.Metadata.ToJsonString(NightingaleJson.Options)));
    }

    [Fact]
    public async Task ReadByOrdinal_WhenTheStoreHasNoOrdinals_ShouldRefuseWithoutQuerying()
    {
        // Arrange: the shared store is initialized without ordinals, so the columns do not exist.
        var sut = database.Store;
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "vso-none");

        // Act & Assert
        sut.OrdinalsEnabled.ShouldBeFalse();
        (await Should.ThrowAsync<OrdinalsNotEnabledException>(() => sut.ReadByOrdinalAsync(orders, Direction.Forwards, 0, 1, TestContext.Current.CancellationToken))).Stream.ShouldBe("$ce-vso-none");
        await Should.ThrowAsync<OrdinalsNotEnabledException>(() => sut.OrdinalHeadAsync(orders, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<OrdinalsNotEnabledException>(() => sut.NumberedThroughAsync(TestContext.Current.CancellationToken));
    }

    private static (string Orders, string Shipments, string Placed) Names()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ("vso" + suffix, "vss" + suffix, "placed_" + suffix);
    }

    private static EventData Event(string type) => new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    /// <summary>Waits for the tail to reach a position, so a virtual read bounded by the head sees the event.</summary>
    private async Task<long> ReachAsync(long position)
    {
        var tail = database.Services.GetRequiredService<IStoreTail>();
        var head = await tail.RefreshAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (head < position)
        {
            head = await tail.WaitForAdvanceAsync(head, timeout.Token);
        }

        return head;
    }
}
