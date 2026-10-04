using System.Text;

using Dracocephalum.Nightingale.Server.Polecat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The read-only connection against a real server. There is no secondary here, so the connection
/// reaches the same database, and what is tested is the query and the wiring: the mark and the
/// part of a page below it come back together, the raw path hydrates <c>$all</c> as the store
/// does, and a plain stream is readable through the second store. How a page is put together
/// when the secondary lags is the router's unit tests.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class ReadOnlyConnectionTests(SqlServerTestDatabase database)
{
    private readonly VirtualStreamReader _reader = new(
        () => new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(database.ConnectionString).Options, new EventStoreSchema("dbo")),
        JasperFx.StorageConstants.DefaultTenantId);

    [Fact]
    public async Task ReadBelowMarkAsync_ShouldReturnTheMarkAndThePageUpToTheLowerOfTheMarkAndTheHead()
    {
        // Arrange: three events of a category nothing else writes to.
        var category = "replica" + Guid.NewGuid().ToString("N")[..8];
        var stream = new VirtualStreamName(VirtualStreamKind.Category, category);
        var appended = await database.Store.AppendAsync(category + "-1", StreamState.NoStream, [Event(), Event(), Event()], TestContext.Current.CancellationToken);
        var last = appended.Position;
        await ReachAsync(last);

        // Act
        var (mark, upToHead) = await _reader.ReadBelowMarkAsync(stream, Direction.Forwards, 0, last - 1, 10, TestContext.Current.CancellationToken);
        var (_, all) = await _reader.ReadBelowMarkAsync(stream, Direction.Forwards, 0, long.MaxValue, 10, TestContext.Current.CancellationToken);
        var (_, backwards) = await _reader.ReadBelowMarkAsync(stream, Direction.Backwards, last, long.MaxValue, 10, TestContext.Current.CancellationToken);
        var (aboveMark, none) = await _reader.ReadBelowMarkAsync(stream, Direction.Backwards, long.MaxValue, long.MaxValue, 10, TestContext.Current.CancellationToken);

        // Assert: a backwards page that begins above the mark is not this connection's to answer.
        mark.ShouldBeGreaterThanOrEqualTo(last);
        upToHead.Select(record => record.Position).ShouldBe([last - 2, last - 1]);
        all.Select(record => record.Position).ShouldBe([last - 2, last - 1, last]);
        backwards.Select(record => record.Position).ShouldBe([last, last - 1, last - 2]);
        aboveMark.ShouldBeLessThan(long.MaxValue);
        none.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadBelowMarkAsync_ForAll_ShouldReturnTheSameRecordsAsTheStoreDoes()
    {
        // Arrange: the raw path and the store's path hydrate independently; they must agree byte for byte.
        var name = "replica" + Guid.NewGuid().ToString("N")[..8] + "-1";
        const string body = "{\"OrderId\":1,\"Total\":42.50,\"Note\":\"Café ☕\"}";
        var metadata = System.Text.Json.Nodes.JsonNode.Parse("{\"Source\":\"Checkout\",\"$correlationId\":\"c-1\"}")!.AsObject();
        var appended = await database.Store.AppendAsync(name, StreamState.NoStream, [new EventData(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes(body), metadata)], TestContext.Current.CancellationToken);
        await ReachAsync(appended.Position);

        // Act
        var viaStore = (await database.Store.ReadAsync(name, Direction.Forwards, null, 1, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events.ShouldHaveSingleItem();
        var (_, raw) = await _reader.ReadBelowMarkAsync(null, Direction.Forwards, appended.Position, appended.Position, 1, TestContext.Current.CancellationToken);
        var viaAll = (await database.Store.ReadAllAsync(Direction.Forwards, appended.Position, appended.Position, 1, TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        // Assert
        foreach (var record in new[] { raw.ShouldHaveSingleItem(), viaAll })
        {
            record.ShouldSatisfyAllConditions(
                read => read.Id.ShouldBe(viaStore.Id),
                read => read.Stream.ShouldBe(viaStore.Stream),
                read => read.Revision.ShouldBe(viaStore.Revision),
                read => read.Position.ShouldBe(viaStore.Position),
                read => read.Type.ShouldBe(viaStore.Type),
                read => read.Created.ShouldBe(viaStore.Created),
                read => Encoding.UTF8.GetString(read.Data.Span).ShouldBe(body),
                read => read.Metadata.ToJsonString(NightingaleJson.Options).ShouldBe(viaStore.Metadata.ToJsonString(NightingaleJson.Options)));
        }
    }

    [Fact]
    public async Task ReadEventualAsync_WhenStreamsAreReadFromTheReadOnlyConnection_ShouldReturnTheStream()
    {
        // Arrange: a second host over the same database, asked to read plain streams the other way.
        var name = "replica" + Guid.NewGuid().ToString("N")[..8] + "-1";
        await database.Store.AppendAsync(name, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        using var host = await TestDatabases.StartHostAsync(database.Name, options => options.ReadStreamsFromReadOnlyConnection = true);
        try
        {
            // Act
            var slice = await host.Store().ReadEventualAsync(name, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
            var missing = await host.Store().ReadEventualAsync(name + "-never", Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

            // Assert
            slice.ShouldNotBeNull().Events.Select(record => record.Revision).ShouldBe([0, 1]);
            slice.Head.Last.ShouldBe(1);
            missing.ShouldBeNull();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldSeekTheIndexDeclaredForItAndNeverScanIt()
    {
        // Arrange: the queries are LINQ now, so what the database does with them is asked of the
        // database: its own count of seeks and scans on the category index.
        var category = "replica" + Guid.NewGuid().ToString("N")[..8];
        var stream = new VirtualStreamName(VirtualStreamKind.Category, category);
        var appended = await database.Store.AppendAsync(category + "-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        const string Index = "ix_pc_events_category_seq";
        var before = await IndexUseAsync(Index);

        // Act: a page, the bounds and a count, each by tenant, category and position.
        var page = await _reader.ReadAsync(stream, Direction.Forwards, 0, appended.Position, 10, TestContext.Current.CancellationToken);
        var afterPage = await IndexUseAsync(Index);
        var head = await _reader.HeadAsync(stream, appended.Position, TestContext.Current.CancellationToken);
        var afterHead = await IndexUseAsync(Index);
        var count = await _reader.CountAsync(stream, 0, appended.Position, TestContext.Current.CancellationToken);
        var after = await IndexUseAsync(Index);

        // Assert
        page.Count.ShouldBe(2);
        head.ShouldBe(new StreamHead(appended.Position - 1, appended.Position));
        count.ShouldBe(2);

        // The bounds and the count are answered from the index alone, so they seek it at any size.
        // The page also needs columns the index does not hold, and on a table this small the
        // database reads the table itself instead; its predicate is the same one, so what is
        // asserted for it is that it never scans the index.
        afterPage.Scans.ShouldBe(before.Scans);
        (afterHead.Seeks - afterPage.Seeks).ShouldBe(2, "the bounds are a seek from each end");
        (after.Seeks - afterHead.Seeks).ShouldBe(1, "the count is one seek");
        after.Scans.ShouldBe(before.Scans);
    }

    private async Task<(long Seeks, long Scans)> IndexUseAsync(string index)
    {
        const string Sql = "SELECT ISNULL(SUM(s.{0}), 0) FROM sys.indexes i LEFT JOIN sys.dm_db_index_usage_stats s ON s.object_id = i.object_id AND s.index_id = i.index_id AND s.database_id = DB_ID() WHERE i.name = '{1}'";
        var seeks = await TestDatabases.ScalarAsync<long>(database.Name, string.Format(System.Globalization.CultureInfo.InvariantCulture, Sql, "user_seeks", index));
        var scans = await TestDatabases.ScalarAsync<long>(database.Name, string.Format(System.Globalization.CultureInfo.InvariantCulture, Sql, "user_scans", index));
        return (seeks, scans);
    }

    private static EventData Event() =>
        new(Guid.NewGuid(), "order_placed", "{}"u8.ToArray(), []);

    /// <summary>Waits until the stored mark, as the connection sees it, covers a position.</summary>
    private async Task ReachAsync(long position)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var (mark, _) = await _reader.ReadBelowMarkAsync(null, Direction.Forwards, position, position, 1, TestContext.Current.CancellationToken);
            if (mark >= position)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The high-water mark did not reach {position}.");
    }
}
