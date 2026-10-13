using System.Text;

using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

    private static readonly EventStoreSchema Schema = new("dbo");

    private readonly InMemoryDatabaseRoot _root = new();
    private readonly DbContextOptions<EventsDbContext> _options;

    private readonly VirtualStreamReader _sut;

    public VirtualStreamReaderTests()
    {
        _options = new DbContextOptionsBuilder<EventsDbContext>().UseInMemoryDatabase("events", _root).Options;
        _sut = new VirtualStreamReader(() => new EventsDbContext(_options, Schema), Tenant);
        using var context = new Seeding(_root);
        context.Set<EventRow>().AddRange(
            Row(1, "orders-1", 1, "order_placed", categoryOrdinal: 0, typeOrdinal: 0),
            Row(2, "shipments-1", 1, "shipment_sent"),
            Row(3, "orders-1", 2, "order_paid", categoryOrdinal: 1),
            Row(4, "orders-2", 1, "order_placed", archived: true, categoryOrdinal: 2, typeOrdinal: 1),
            Row(5, "orders-3", 1, "order_placed", tenant: "tenant-b", categoryOrdinal: 0, typeOrdinal: 0),
            Row(6, "orders-4", 1, "order_placed", categoryOrdinal: 3, typeOrdinal: 2),
            Row(7, "orders-4", 2, "order_paid", categoryOrdinal: 4),
            Row(8, "orders-4", 3, "order_shipped"));
        context.Set<ProgressionRow>().Add(new ProgressionRow { Name = EventsDbContext.HighWaterMark, LastSeqId = 6 });
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
    public async Task ReadAllAsync_WithNoTenant_ShouldSpanAllTenantsAndWithOneStayWithinIt()
    {
        // Arrange
        var every = new VirtualStreamReader(() => new EventsDbContext(_options, Schema), null);

        // Act
        var own = await _sut.ReadAllAsync(Direction.Forwards, 1, 100, 10, TestContext.Current.CancellationToken);
        var all = await every.ReadAllAsync(Direction.Forwards, 1, 100, 10, TestContext.Current.CancellationToken);
        var head = await every.HeadAsync(Orders, 100, TestContext.Current.CancellationToken);

        // Assert: the archived event at 4 is in neither; the other tenant's at 5 is in the wildcard's.
        own.Select(record => record.Position).ShouldBe([1, 2, 3, 6, 7, 8]);
        all.Select(record => record.Position).ShouldBe([1, 2, 3, 5, 6, 7, 8]);
        head.ShouldBe(new StreamHead(1, 8));
    }

    [Fact]
    public async Task ScanAllAsync_WithAStreamPrefix_ShouldKeepTheTenantsLiveEventsUnderItAndSayHowFarItLooked()
    {
        // Arrange: a stream named just the category, which the category holds and the prefix does
        // not, beside the seeded ones.
        using (var context = new Seeding(_root))
        {
            context.Set<EventRow>().Add(Row(9, "orders", 1, "order_placed"));
            context.SaveChanges();
        }

        var orders = EventFilter.StreamName("orders-");

        // Act
        var whole = await _sut.ScanAllAsync(orders, Direction.Forwards, 1, 100, 100, 10, TestContext.Current.CancellationToken);
        var counted = await _sut.ScanAllAsync(orders, Direction.Forwards, 1, 100, 100, 2, TestContext.Current.CancellationToken);
        var windowed = await _sut.ScanAllAsync(orders, Direction.Forwards, 1, 100, 4, 10, TestContext.Current.CancellationToken);
        var toTheHead = await _sut.ScanAllAsync(orders, Direction.Forwards, 7, 8, 100, 10, TestContext.Current.CancellationToken);
        var backwards = await _sut.ScanAllAsync(orders, Direction.Backwards, 8, 8, 3, 10, TestContext.Current.CancellationToken);

        // Assert: not the shipment at 2, the deleted stream's event at 4, the other tenant's at 5
        // nor the bare name at 9; the stretch ends at the window, the head, or the last event kept
        // when as many were kept as asked for.
        whole.Events.Select(record => record.Position).ShouldBe([1, 3, 6, 7, 8]);
        whole.Through.ShouldBe(100);
        counted.Events.Select(record => record.Position).ShouldBe([1, 3]);
        counted.Through.ShouldBe(3);
        windowed.Events.Select(record => record.Position).ShouldBe([1, 3]);
        windowed.Through.ShouldBe(4);
        toTheHead.Events.Select(record => record.Position).ShouldBe([7, 8]);
        toTheHead.Through.ShouldBe(8);
        backwards.Events.Select(record => record.Position).ShouldBe([8, 7, 6]);
        backwards.Through.ShouldBe(6);
    }

    [Fact]
    public async Task ScanAllAsync_ByTypePrefixAndByRegularExpression_ShouldKeepWhatMatches()
    {
        // Arrange
        var placedOrPaid = EventFilter.EventType("order_p");
        var ordersTwoOrFour = EventFilter.StreamNameMatches("^orders-[24]$");
        var paidAnywhere = EventFilter.EventTypeMatches("paid$");

        // Act
        var byType = await _sut.ScanAllAsync(placedOrPaid, Direction.Forwards, 1, 100, 100, 10, TestContext.Current.CancellationToken);
        var byStreamRegex = await _sut.ScanAllAsync(ordersTwoOrFour, Direction.Forwards, 1, 100, 100, 10, TestContext.Current.CancellationToken);
        var byTypeRegexCounted = await _sut.ScanAllAsync(paidAnywhere, Direction.Forwards, 1, 100, 100, 1, TestContext.Current.CancellationToken);
        var nothing = await _sut.ScanAllAsync(EventFilter.StreamNameMatches("^invoices-"), Direction.Forwards, 1, 100, 5, 10, TestContext.Current.CancellationToken);

        // Assert: the regular expression is matched in process, and the events it kept come back
        // hydrated like any other, in position order, as many as asked for.
        byType.Events.Select(record => record.Position).ShouldBe([1, 3, 6, 7]);
        byStreamRegex.Events.Select(record => record.Position).ShouldBe([6, 7, 8]);
        byTypeRegexCounted.Events.ShouldHaveSingleItem().Position.ShouldBe(3);
        byTypeRegexCounted.Through.ShouldBe(3);
        byTypeRegexCounted.Events[0].Stream.ShouldBe("orders-1");
        nothing.Events.ShouldBeEmpty();
        nothing.Through.ShouldBe(5);
    }

    [Fact]
    public async Task ReadPointersAsync_ShouldPointAtTheTenantsLiveEventsInPositionOrderWithinTheBounds()
    {
        // Act
        var all = await _sut.ReadPointersAsync(1, 100, 10, TestContext.Current.CancellationToken);
        var bounded = await _sut.ReadPointersAsync(2, 6, 2, TestContext.Current.CancellationToken);

        // Assert: the deleted stream's event at 4 and the other tenant's at 5 are not pointed at.
        all.ShouldBe([new EventPointer("orders-1", 0, 1, "order_placed"), new EventPointer("shipments-1", 0, 2, "shipment_sent"), new EventPointer("orders-1", 1, 3, "order_paid"), new EventPointer("orders-4", 0, 6, "order_placed"), new EventPointer("orders-4", 1, 7, "order_paid"), new EventPointer("orders-4", 2, 8, "order_shipped")]);
        bounded.ShouldBe([new EventPointer("shipments-1", 0, 2, "shipment_sent"), new EventPointer("orders-1", 1, 3, "order_paid")]);
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
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<EventsDbContext>().UseInMemoryDatabase("events", root).Options;
        await using (var context = new Seeding(root))
        {
            context.Set<EventRow>().Add(Row(1, "orders-1", 1, "order_placed"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var (mark, events) = await new VirtualStreamReader(() => new EventsDbContext(options, Schema), Tenant).ReadBelowMarkAsync(Orders, Direction.Forwards, 1, 100, 10, TestContext.Current.CancellationToken);

        // Assert
        mark.ShouldBe(0);
        events.ShouldBeEmpty();
    }

    /// <summary>
    /// Writes the rows the mirror reads. The mirror itself refuses to save, so the table is
    /// filled through a context of the test's own over the same in-memory database root.
    /// </summary>
    private sealed class Seeding(InMemoryDatabaseRoot root) : DbContext(new DbContextOptionsBuilder<Seeding>().UseInMemoryDatabase("events", root).Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EventRow>().HasKey(row => row.SeqId);
            modelBuilder.Entity<ProgressionRow>().HasKey(row => row.Name);
        }
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
            Category = stream.Contains('-', StringComparison.Ordinal) ? stream[..stream.IndexOf('-', StringComparison.Ordinal)] : stream,
            CategoryOrdinal = categoryOrdinal,
            TypeOrdinal = typeOrdinal,
        };
}
