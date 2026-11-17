using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The virtual streams by correlation and the listings of streams against the local SQL Server:
/// what each holds, that each bound and count seeks the filtered index declared for it, that an
/// event without a correlation id adds nothing to the correlation's index, and the exact filters
/// on <c>$all</c>. The shared database has other tests' events in it, so every read is bounded by
/// these tests' own positions and checked against these tests' own streams.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class ListingTests(SqlServerTestDatabase database)
{
    [Fact]
    public async Task Read_OfACorrelation_ShouldHoldItsEventsAcrossStreamsInPositionOrderAndSeekItsIndex()
    {
        // Arrange: one workflow over two streams, and an event of another.
        var sut = database.Store;
        var workflow = "checkout-" + Guid.NewGuid().ToString("N");
        var orders = NewName("orders");
        await sut.AppendAsync(orders, StreamState.NoStream, [Correlated(workflow), Correlated("other-" + workflow)], TestContext.Current.CancellationToken);
        var last = await sut.AppendAsync(NewName("payments"), StreamState.NoStream, [Correlated(workflow)], TestContext.Current.CancellationToken);
        var firstPosition = await FirstPositionAsync(orders);
        var stream = new VirtualStreamName(VirtualStreamKind.Correlation, workflow);
        const string Index = "ix_pc_events_correlation_seq";
        var before = await IndexUseAsync(Index);

        // Act
        var head = await sut.VirtualHeadAsync(stream, last.Position, TestContext.Current.CancellationToken);
        var count = await sut.CountVirtualAsync(stream, 0, last.Position, TestContext.Current.CancellationToken);
        var after = await IndexUseAsync(Index);
        var events = await sut.ReadVirtualAsync(stream, Direction.Forwards, 1, last.Position, 10, TestContext.Current.CancellationToken);

        // Assert: the bounds and the count are the index's alone, so they seek it at any size.
        events.Select(e => e.Position).ShouldBe([firstPosition, last.Position]);
        events.ShouldAllBe(e => e.Metadata[MetadataKeys.CorrelationId]!.GetValue<string>() == workflow);
        head.ShouldBe(new StreamHead(firstPosition, last.Position));
        count.ShouldBe(2);
        (after.Seeks - before.Seeks).ShouldBe(3, "the bounds are a seek from each end, the count one more");
        after.Scans.ShouldBe(before.Scans);
    }

    [Theory]
    [InlineData("ix_pc_events_correlation_seq", "[correlation_id] IS NOT NULL")]
    [InlineData("ix_pc_events_stream_starts", "[version]=(1)")]
    [InlineData("ix_pc_events_category_starts", "[version]=(1)")]
    public async Task Indexes_OfTheCorrelationsAndTheListings_ShouldHoldOnlyTheRowsTheirFilterNames(string index, string filter)
    {
        // Act: the database holds a filtered index's rows for the rows its filter keeps and writes
        // nothing to it for any other, so an append without a correlation id, or past a stream's
        // first event, costs these indexes nothing; what is the gateway's to hold is that the
        // indexes as created carry the filters.
        var definition = await TestDatabases.ScalarAsync<string>(database.Name, string.Format(CultureInfo.InvariantCulture, "SELECT filter_definition FROM sys.indexes WHERE name = '{0}'", index));

        // Assert
        definition.ShouldContain(filter);
        definition.ShouldContain("[is_archived]=(0)");
    }

    [Fact]
    public async Task Read_OfTheStreamsAndOfACategorysStreams_ShouldHoldEachStreamsFirstEventAndSeekTheirIndexes()
    {
        // Arrange: two streams of one category and one of another, each moved on past its start,
        // and a stream named just the category, which is in it.
        var sut = database.Store;
        var category = "lst" + Guid.NewGuid().ToString("N")[..12];
        await sut.AppendAsync(category + "-1", StreamState.NoStream, [Plain(), Plain()], TestContext.Current.CancellationToken);
        await sut.AppendAsync(category + "-1", StreamState.Any, [Plain()], TestContext.Current.CancellationToken);
        await sut.AppendAsync("other" + category + "-1", StreamState.NoStream, [Plain()], TestContext.Current.CancellationToken);
        await sut.AppendAsync(category, StreamState.NoStream, [Plain()], TestContext.Current.CancellationToken);
        var last = await sut.AppendAsync(category + "-2", StreamState.NoStream, [Plain(), Plain()], TestContext.Current.CancellationToken);
        var from = await FirstPositionAsync(category + "-1");
        var lastStart = await FirstPositionAsync(category + "-2");
        var streams = new VirtualStreamName(VirtualStreamKind.Streams, string.Empty);
        var ofCategory = new VirtualStreamName(VirtualStreamKind.CategoryStreams, category);
        var streamStarts = await IndexUseAsync("ix_pc_events_stream_starts");
        var categoryStarts = await IndexUseAsync("ix_pc_events_category_starts");

        // Act
        var categoryHead = await sut.VirtualHeadAsync(ofCategory, last.Position, TestContext.Current.CancellationToken);
        var categoryCount = await sut.CountVirtualAsync(ofCategory, 0, last.Position, TestContext.Current.CancellationToken);
        var streamsCount = await sut.CountVirtualAsync(streams, from - 1, last.Position, TestContext.Current.CancellationToken);
        var afterStreamStarts = await IndexUseAsync("ix_pc_events_stream_starts");
        var afterCategoryStarts = await IndexUseAsync("ix_pc_events_category_starts");
        var listed = await sut.ReadVirtualAsync(streams, Direction.Forwards, from, last.Position, 100, TestContext.Current.CancellationToken);
        var ofCategoryListed = await sut.ReadVirtualAsync(ofCategory, Direction.Forwards, 1, last.Position, 10, TestContext.Current.CancellationToken);

        // Assert: one event a stream, its first, in the order the streams began.
        listed.Where(e => e.Stream.Contains(category, StringComparison.Ordinal)).Select(e => (e.Stream, e.Revision)).ShouldBe([(category + "-1", 0L), ("other" + category + "-1", 0L), (category, 0L), (category + "-2", 0L)]);
        ofCategoryListed.Select(e => e.Stream).ShouldBe([category + "-1", category, category + "-2"]);
        ofCategoryListed.ShouldAllBe(e => e.Revision == 0);
        categoryHead.ShouldBe(new StreamHead(from, lastStart));
        categoryCount.ShouldBe(3);
        streamsCount.ShouldBeGreaterThanOrEqualTo(4);
        (afterCategoryStarts.Seeks - categoryStarts.Seeks).ShouldBe(3, "the category's bounds are a seek from each end, its count one more");
        (afterStreamStarts.Seeks - streamStarts.Seeks).ShouldBe(1, "the listing's count is one seek");
        afterCategoryStarts.Scans.ShouldBe(categoryStarts.Scans);
        afterStreamStarts.Scans.ShouldBe(streamStarts.Scans);
    }

    [Fact]
    public async Task ScanAll_WithACategoryOrAnExactTypeFilter_ShouldKeepTheWholeCategoryAndTheTypeAlone()
    {
        // Arrange: the stream named just the category is in it, a prefix of the category is not,
        // and a type is not its extension.
        var sut = database.Store;
        var category = "flx" + Guid.NewGuid().ToString("N")[..12];
        var first = await sut.AppendAsync(category + "-1", StreamState.NoStream, [Typed(category + "_placed")], TestContext.Current.CancellationToken);
        await sut.AppendAsync(category, StreamState.NoStream, [Typed(category + "_placed_v2")], TestContext.Current.CancellationToken);
        var last = await sut.AppendAsync(category + "x-1", StreamState.NoStream, [Typed(category + "_placed")], TestContext.Current.CancellationToken);
        var from = first.Position;

        // Act
        var byCategory = await sut.ScanAllAsync(StreamFilter.Category(category), Direction.Forwards, from, last.Position, 10_000, 100, TestContext.Current.CancellationToken);
        var byPrefix = await sut.ScanAllAsync(StreamFilter.Prefix(category + "-"), Direction.Forwards, from, last.Position, 10_000, 100, TestContext.Current.CancellationToken);
        var byType = await sut.ScanAllAsync(EventTypeFilter.Type(category + "_placed"), Direction.Forwards, from, last.Position, 10_000, 100, TestContext.Current.CancellationToken);

        // Assert
        byCategory.Events.Select(e => e.Stream).ShouldBe([category + "-1", category]);
        byPrefix.Events.Select(e => e.Stream).ShouldBe([category + "-1"]);
        byType.Events.Select(e => e.Stream).ShouldBe([category + "-1", category + "x-1"]);
    }

    private static string NewName(string category) => category + "-" + Guid.NewGuid().ToString("N");

    /// <summary>The position of a stream's first event, read rather than worked out: other tests append at once, so an append's positions need not be consecutive.</summary>
    private async Task<long> FirstPositionAsync(string stream) =>
        (await database.Store.ReadAsync(stream, Direction.Forwards, null, 1, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events[0].Position;

    private static EventData Plain() => new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{}"));

    private static EventData Typed(string type) => new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{}"));

    private static EventData Correlated(string correlationId) =>
        new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{}"), new JsonObject { [MetadataKeys.CorrelationId] = correlationId });

    private async Task<(long Seeks, long Scans)> IndexUseAsync(string index)
    {
        const string Sql = "SELECT ISNULL(SUM(s.{0}), 0) FROM sys.indexes i LEFT JOIN sys.dm_db_index_usage_stats s ON s.object_id = i.object_id AND s.index_id = i.index_id AND s.database_id = DB_ID() WHERE i.name = '{1}'";
        var seeks = await TestDatabases.ScalarAsync<long>(database.Name, string.Format(CultureInfo.InvariantCulture, Sql, "user_seeks", index));
        var scans = await TestDatabases.ScalarAsync<long>(database.Name, string.Format(CultureInfo.InvariantCulture, Sql, "user_scans", index));
        return (seeks, scans);
    }
}
