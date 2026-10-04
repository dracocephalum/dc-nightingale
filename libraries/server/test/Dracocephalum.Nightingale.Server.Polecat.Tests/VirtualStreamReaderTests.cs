using System.Text;

using Dracocephalum.Nightingale.Server.Polecat.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The reader's queries on the in-memory provider: which rows a read takes, in what order, and
/// where it stops. The table holds two categories, two event types, a second tenant and one
/// deleted stream, so every bound has something on the wrong side of it. That the database
/// answers these with a seek, and matches names by its own collation, is the integration
/// suite's to say; the in-memory provider compares names exactly.
/// </summary>
public sealed class VirtualStreamReaderTests
{
    private const string Tenant = "tenant-a";
    private static readonly VirtualStreamName Orders = new(VirtualStreamKind.Category, "orders");
    private static readonly VirtualStreamName Placed = new(VirtualStreamKind.EventType, "order_placed");

    private readonly DbContextOptions<EventsDbContext> _options = new DbContextOptionsBuilder<EventsDbContext>()
        .UseInMemoryDatabase("events-" + Guid.NewGuid().ToString("N"))
        .Options;

    private readonly VirtualStreamReader _sut;

    public VirtualStreamReaderTests()
    {
        _sut = new VirtualStreamReader(_options, "dbo", Tenant);
        using var context = new EventsDbContext(_options, "dbo");
        context.Events.AddRange(
            Row(1, "orders-1", 1, "order_placed", categoryOrdinal: 0, typeOrdinal: 0),
            Row(2, "shipments-1", 1, "shipment_sent"),
            Row(3, "orders-1", 2, "order_paid", categoryOrdinal: 1),
            Row(4, "orders-2", 1, "order_placed", archived: true, categoryOrdinal: 2, typeOrdinal: 1),
            Row(5, "orders-3", 1, "order_placed", tenant: "tenant-b", categoryOrdinal: 0, typeOrdinal: 0),
            Row(6, "orders-4", 1, "order_placed", categoryOrdinal: 3, typeOrdinal: 2),
            Row(7, "orders-4", 2, "order_paid", categoryOrdinal: 4),
            Row(8, "orders-4", 3, "order_shipped"));
        context.Progression.Add(new ProgressionRow { Name = EventsDbContext.HighWaterMark, LastSeqId = 6 });
        context.SaveChanges();
    }

    [Fact]
    public async Task HeadAsync_ShouldBeTheFirstAndLastLiveEventOfTheTenantAtOrBelowTheHead()
    {
        // Act
        var all = await _sut.HeadAsync(Orders, 100, TestContext.Current.CancellationToken);
        var bounded = await _sut.HeadAsync(Orders, 5, TestContext.Current.CancellationToken);
        var none = await _sut.HeadAsync(new VirtualStreamName(VirtualStreamKind.Category, "invoices"), 100, TestContext.Current.CancellationToken);

        // Assert: the deleted stream's event at 4 and the other tenant's at 5 are not this stream's.
        all.ShouldBe(new StreamHead(1, 8));
        bounded.ShouldBe(new StreamHead(1, 3));
        none.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_ShouldPageInPositionOrderWithinTheBounds()
    {
        // Act
        var forwards = await _sut.ReadAsync(Orders, Direction.Forwards, 3, 7, 10, TestContext.Current.CancellationToken);
        var firstTwo = await _sut.ReadAsync(Orders, Direction.Forwards, 0, 100, 2, TestContext.Current.CancellationToken);
        var backwards = await _sut.ReadAsync(Orders, Direction.Backwards, 7, 100, 2, TestContext.Current.CancellationToken);
        var byType = await _sut.ReadAsync(Placed, Direction.Forwards, 0, 100, 10, TestContext.Current.CancellationToken);

        // Assert
        forwards.Select(record => record.Position).ShouldBe([3, 6, 7]);
        firstTwo.Select(record => record.Position).ShouldBe([1, 3]);
        backwards.Select(record => record.Position).ShouldBe([7, 6]);
        byType.Select(record => record.Stream).ShouldBe(["orders-1", "orders-4"]);
        forwards.ShouldAllBe(record => record.Ordinal == null);
    }

    [Fact]
    public async Task ReadAsync_ShouldHydrateARecordAsTheContractNumbersIt()
    {
        // Act
        var record = (await _sut.ReadAsync(Orders, Direction.Forwards, 3, 3, 1, TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        // Assert: the store's version is one-based, the contract's revision zero-based; the two
        // reserved metadata keys come last.
        record.Stream.ShouldBe("orders-1");
        record.Revision.ShouldBe(1);
        record.Position.ShouldBe(3);
        record.Type.ShouldBe("order_paid");
        Encoding.UTF8.GetString(record.Data.Span).ShouldBe("{\"n\":3}");
        record.Metadata.ToJsonString().ShouldBe("{\"source\":\"test\",\"$correlationId\":\"c-3\",\"$causationId\":\"k-3\"}");
    }

    [Fact]
    public async Task CountAsync_ShouldCountLiveEventsAfterAPositionUpToTheHead()
    {
        // Act
        var count = await _sut.CountAsync(Orders, 1, 7, TestContext.Current.CancellationToken);

        // Assert: 3, 6 and 7; not 4, which is deleted, nor 8, which is above the head.
        count.ShouldBe(3);
    }

    [Fact]
    public async Task OrdinalHeadAsync_ShouldCountAHoleAsANumberInTheSequence()
    {
        // Act
        var category = await _sut.OrdinalHeadAsync(Orders, TestContext.Current.CancellationToken);
        var type = await _sut.OrdinalHeadAsync(Placed, TestContext.Current.CancellationToken);
        var none = await _sut.OrdinalHeadAsync(new VirtualStreamName(VirtualStreamKind.Category, "shipments"), TestContext.Current.CancellationToken);

        // Assert: the deleted event keeps its ordinal, 2 in the category; the unnumbered event at 8 has none.
        category.ShouldBe(new StreamHead(0, 4));
        type.ShouldBe(new StreamHead(0, 2));
        none.ShouldBeNull();
    }

    [Fact]
    public async Task ReadByOrdinalAsync_ShouldPageLiveNumberedEventsInOrdinalOrderEachCarryingItsOrdinal()
    {
        // Act
        var forwards = await _sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 1, 10, TestContext.Current.CancellationToken);
        var backwards = await _sut.ReadByOrdinalAsync(Orders, Direction.Backwards, 3, 2, TestContext.Current.CancellationToken);
        var byType = await _sut.ReadByOrdinalAsync(Placed, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);

        // Assert: ordinal 2 is the deleted event, a hole; the event at 8 is not numbered yet.
        forwards.Select(record => record.Ordinal).ShouldBe([1, 3, 4]);
        backwards.Select(record => record.Ordinal).ShouldBe([3, 1]);
        byType.Select(record => (record.Position, record.Ordinal)).ShouldBe([(1, 0), (6, 2)]);
    }

    [Fact]
    public async Task ReadBelowMarkAsync_Forwards_ShouldStopAtTheLowerOfTheMarkAndTheHead()
    {
        // Act: the mark is 6.
        var (mark, toMark) = await _sut.ReadBelowMarkAsync(Orders, Direction.Forwards, 1, 100, 10, TestContext.Current.CancellationToken);
        var (_, toHead) = await _sut.ReadBelowMarkAsync(Orders, Direction.Forwards, 1, 3, 10, TestContext.Current.CancellationToken);
        var (_, above) = await _sut.ReadBelowMarkAsync(Orders, Direction.Forwards, 7, 100, 10, TestContext.Current.CancellationToken);
        var (_, all) = await _sut.ReadBelowMarkAsync(null, Direction.Forwards, 1, 100, 10, TestContext.Current.CancellationToken);

        // Assert: $all is every live event of the tenant, whatever its stream.
        mark.ShouldBe(6);
        toMark.Select(record => record.Position).ShouldBe([1, 3, 6]);
        toHead.Select(record => record.Position).ShouldBe([1, 3]);
        above.ShouldBeEmpty();
        all.Select(record => record.Position).ShouldBe([1, 2, 3, 6]);
    }

    [Fact]
    public async Task ReadBelowMarkAsync_Backwards_ShouldAnswerOnlyWhenThePageBeginsAtOrBelowTheMark()
    {
        // Act
        var (_, covered) = await _sut.ReadBelowMarkAsync(Orders, Direction.Backwards, 6, 100, 2, TestContext.Current.CancellationToken);
        var (mark, aboveMark) = await _sut.ReadBelowMarkAsync(Orders, Direction.Backwards, 7, 100, 2, TestContext.Current.CancellationToken);

        // Assert: between the mark and 7 this connection cannot say what there is.
        covered.Select(record => record.Position).ShouldBe([6, 3]);
        mark.ShouldBe(6);
        aboveMark.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadBelowMarkAsync_WhenTheMarkWasNeverWritten_ShouldAnswerNothing()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<EventsDbContext>().UseInMemoryDatabase("events-" + Guid.NewGuid().ToString("N")).Options;
        await using (var context = new EventsDbContext(options, "dbo"))
        {
            context.Events.Add(Row(1, "orders-1", 1, "order_placed"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var (mark, events) = await new VirtualStreamReader(options, "dbo", Tenant).ReadBelowMarkAsync(Orders, Direction.Forwards, 1, 100, 10, TestContext.Current.CancellationToken);

        // Assert
        mark.ShouldBe(0);
        events.ShouldBeEmpty();
    }

    private static EventRow Row(long position, string stream, long version, string type, string tenant = Tenant, bool archived = false, long? categoryOrdinal = null, long? typeOrdinal = null) =>
        new()
        {
            SeqId = position,
            Id = Guid.NewGuid(),
            StreamId = stream,
            Version = version,
            Data = $"{{\"n\":{position}}}",
            Type = type,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(position),
            TenantId = tenant,
            CorrelationId = $"c-{position}",
            CausationId = $"k-{position}",
            Headers = "{\"source\":\"test\"}",
            IsArchived = archived,
            Category = stream[..stream.IndexOf('-', StringComparison.Ordinal)],
            CategoryOrdinal = categoryOrdinal,
            TypeOrdinal = typeOrdinal,
        };
}
