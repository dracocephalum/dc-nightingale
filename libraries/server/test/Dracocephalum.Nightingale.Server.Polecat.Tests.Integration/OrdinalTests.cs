using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The virtual streams under ordinal numbering, against a store initialized with ordinals: the
/// sequencer runs in the host exactly as it would in production, behind the tail, on its lease,
/// and the tests wait for its progress rather than numbering anything themselves. Every test uses
/// a database of its own, dropped at the end whatever happened.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrdinalTests : IAsyncLifetime
{
    /// <summary>The links' key, which a read seeks, as the gateway's migration names it.</summary>
    private const string LinkReadIndex = "PK_OrdinalLink";
    private static readonly VirtualStreamName Orders = new(VirtualStreamKind.Category, "orders");
    private static readonly VirtualStreamName Placed = new(VirtualStreamKind.EventType, "order_placed");
    private readonly string _name = TestDatabases.NewName();
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        _host = await TestDatabases.StartProvisionedHostAsync(_name, options =>
        {
            options.Store.AssignOrdinals = true;
            options.Deletion.Allowed = DeletionLevel.Delete;

            // A short lease, so the sequencer's quiet-store pass, on renewal, comes within a second.
            options.OrdinalSequencerLeaseDuration = TimeSpan.FromSeconds(3);

            // A tail that looks only when an append here wakes it, never on its own, so that a
            // count of the events table's scans around a read counts the read alone: the store's
            // high-water agent reads the table's highest position at every look, which is a scan.
            options.TailPollingInterval = TimeSpan.FromMinutes(5);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await TestDatabases.DropAsync(_name);
    }

    [Fact]
    public async Task Sequencer_ShouldNumberEachCategoryAndTypeDenselyInPositionOrder()
    {
        // Arrange
        var sut = Store;
        await sut.AppendAsync("orders-1", StreamState.NoStream, [Event("order_placed"), Event("order_paid")], TestContext.Current.CancellationToken);
        await sut.AppendAsync("shipments-1", StreamState.NoStream, [Event("shipment_dispatched")], TestContext.Current.CancellationToken);
        var last = await sut.AppendAsync("orders-2", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await NumberedAsync(last.Position);

        // Act
        var byCategory = await sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var byType = await sut.ReadByOrdinalAsync(Placed, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var backwards = await sut.ReadByOrdinalAsync(Orders, Direction.Backwards, 2, 2, TestContext.Current.CancellationToken);
        var bounds = await sut.OrdinalHeadAsync(Orders, TestContext.Current.CancellationToken);
        var none = await sut.OrdinalHeadAsync(new VirtualStreamName(VirtualStreamKind.Category, "nothing"), TestContext.Current.CancellationToken);

        // Assert: dense from zero per key, in position order, positions still the global ones.
        sut.OrdinalsEnabled.ShouldBeTrue();
        byCategory.Select(record => record.Ordinal).ShouldBe([0L, 1L, 2L]);
        byCategory.Select(record => record.Stream).ShouldBe(["orders-1", "orders-1", "orders-2"]);
        byCategory.Select(record => record.Position).ShouldBe([1L, 2L, 4L]);
        byType.Select(record => record.Ordinal).ShouldBe([0L, 1L]);
        byType.Select(record => record.Stream).ShouldBe(["orders-1", "orders-2"]);
        backwards.Select(record => record.Ordinal).ShouldBe([2L, 1L]);
        bounds.ShouldBe(new StreamHead(0, 2));
        none.ShouldBeNull();
    }

    [Fact]
    public async Task Sequencer_WhenAStreamIsRemoved_ShouldLeaveAHoleAndNeverReuseItsOrdinals()
    {
        // Arrange: three orders numbered 0, 1, 2; the middle one is deleted, then a fourth arrives.
        var sut = Store;
        await sut.AppendAsync("orders-1", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await sut.AppendAsync("orders-2", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        var third = await sut.AppendAsync("orders-3", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await NumberedAsync(third.Position);
        await sut.ArchiveAsync("orders-2", StreamState.Any, TestContext.Current.CancellationToken);

        // Act
        var withHole = await sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var fourth = await sut.AppendAsync("orders-4", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await NumberedAsync(fourth.Position);
        var afterFourth = await sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        await sut.DeleteAsync("orders-1", StreamState.Any, TestContext.Current.CancellationToken);
        var afterTombstone = await sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var bounds = await sut.OrdinalHeadAsync(Orders, TestContext.Current.CancellationToken);

        // Assert: the deleted event's ordinal is skipped, not reassigned; the bounds still count the
        // archived hole, because its row keeps its number, and no longer the tombstoned one, whose
        // row is gone.
        withHole.Select(record => record.Ordinal).ShouldBe([0L, 2L]);
        afterFourth.Select(record => record.Ordinal).ShouldBe([0L, 2L, 3L]);
        afterTombstone.Select(record => record.Ordinal).ShouldBe([2L, 3L]);
        bounds.ShouldBe(new StreamHead(1, 3));
    }

    [Fact]
    public async Task Sequencer_ShouldLinkEachCorrelationAndListingDenselyAndReadThemBySeeks()
    {
        // Arrange: one workflow over two streams; an event of no workflow that begins a stream;
        // and an event that does neither, which adds no link.
        var sut = Store;
        await sut.AppendAsync("orders-1", StreamState.NoStream, [Correlated("order_placed", "checkout-1"), Correlated("order_paid", "checkout-1")], TestContext.Current.CancellationToken);
        await sut.AppendAsync("shipments-1", StreamState.NoStream, [Correlated("shipment_dispatched", "checkout-1")], TestContext.Current.CancellationToken);
        await sut.AppendAsync("orders-2", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        var last = await sut.AppendAsync("orders-1", StreamState.Any, [Event("order_shipped")], TestContext.Current.CancellationToken);
        await NumberedAsync(last.Position);
        var workflow = new VirtualStreamName(VirtualStreamKind.Correlation, "checkout-1");
        var streams = new VirtualStreamName(VirtualStreamKind.Streams, string.Empty);
        var orderStreams = new VirtualStreamName(VirtualStreamKind.CategoryStreams, "orders");
        var before = await IndexUseAsync(LinkReadIndex);
        var eventsBefore = await IndexUseAsync("pkey_pc_events_seq_id");

        // Act
        var byWorkflow = await sut.ReadByOrdinalAsync(workflow, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var workflowBounds = await sut.OrdinalHeadAsync(workflow, TestContext.Current.CancellationToken);
        var listed = await sut.ReadByOrdinalAsync(streams, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var ofOrders = await sut.ReadByOrdinalAsync(orderStreams, Direction.Backwards, 1, 10, TestContext.Current.CancellationToken);
        var after = await IndexUseAsync(LinkReadIndex);
        var eventsAfter = await IndexUseAsync("pkey_pc_events_seq_id");
        var links = await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM [nightingale].[OrdinalLink]");

        // Assert: dense from zero per key, in position order; three in the workflow, three
        // streams, each also in its category's listing: nine links, none for the event that did
        // neither. Every read sought the links' key and none scanned it or the events.
        byWorkflow.Select(record => (record.Stream, record.Ordinal)).ShouldBe([("orders-1", 0L), ("orders-1", 1L), ("shipments-1", 2L)]);
        workflowBounds.ShouldBe(new StreamHead(0, 2));
        listed.Select(record => (record.Stream, record.Revision, record.Ordinal)).ShouldBe([("orders-1", 0L, 0L), ("shipments-1", 0L, 1L), ("orders-2", 0L, 2L)]);
        ofOrders.Select(record => (record.Stream, record.Ordinal)).ShouldBe([("orders-2", 1L), ("orders-1", 0L)]);
        links.ShouldBe(9);
        (after.Seeks - before.Seeks).ShouldBeGreaterThanOrEqualTo(5, "two for the bounds, one for each of three pages");
        after.Scans.ShouldBe(before.Scans);
        eventsAfter.Scans.ShouldBe(eventsBefore.Scans, "each linked event is fetched by its key, never by a scan of the table");
    }

    [Fact]
    public async Task Sequencer_WhenAStreamIsDeletedAndItsNameUsedAgain_ShouldLeaveAHoleAndListTheNewStream()
    {
        // Arrange: three order streams listed 0, 1, 2; the middle one is deleted and begun again.
        var sut = Store;
        var orderStreams = new VirtualStreamName(VirtualStreamKind.CategoryStreams, "orders");
        await sut.AppendAsync("orders-1", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await sut.AppendAsync("orders-2", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        var third = await sut.AppendAsync("orders-3", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await NumberedAsync(third.Position);
        await sut.DeleteAsync("orders-2", StreamState.Any, TestContext.Current.CancellationToken);

        // Act
        var again = await sut.AppendAsync("orders-2", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await NumberedAsync(again.Position);
        var listed = await sut.ReadByOrdinalAsync(orderStreams, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var bounds = await sut.OrdinalHeadAsync(orderStreams, TestContext.Current.CancellationToken);

        // Assert: the deleted stream's link points at nothing and is skipped, never reused; the
        // name begun again is a new stream, listed at the next ordinal.
        listed.Select(record => (record.Stream, record.Ordinal)).ShouldBe([("orders-1", 0L), ("orders-3", 2L), ("orders-2", 3L)]);
        bounds.ShouldBe(new StreamHead(0, 3));
    }

    [Fact]
    public async Task Start_Again_ShouldFindTheSchemaStableAndRefuseTurningOrdinalsOff()
    {
        // Arrange: the fixture's host initialized the store with ordinals.
        await _host!.StopAsync(TestContext.Current.CancellationToken);
        _host.Dispose();
        _host = null;

        // Act
        using (var again = await TestDatabases.StartHostAsync(_name, options => options.Store.AssignOrdinals = true))
        {
            await again.StopAsync(TestContext.Current.CancellationToken);
        }

        var refused = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name));

        // Assert
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'AssignOrdinals'")).ShouldBe("true");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.indexes WHERE name IN ('ix_pc_events_category_ordinal', 'ix_pc_events_type_ordinal')")).ShouldBe(2);
        refused.Message.ShouldContain("initialized with ordinals");
    }

    [Fact]
    public async Task Sequencer_WhenTheInstanceThatNumbersStops_ShouldBeTakenOverByAnotherThatGoesOnFromItsNumbers()
    {
        // Arrange: two instances over one store. The lease lasts under a second where a
        // server's lasts thirty, and the renewal a third of that as always, so the one that
        // does not hold it asks again within the time this test is willing to pay for.
        static void Ordinals(NightingaleOptions options)
        {
            options.Store.AssignOrdinals = true;
            options.OrdinalSequencerLeaseDuration = TimeSpan.FromMilliseconds(900);
        }

        var name = TestDatabases.NewName();
        try
        {
            using var first = await TestDatabases.StartProvisionedHostAsync(name, Ordinals);
            using var second = await TestDatabases.StartHostAsync(name, TestDatabases.FastBoot(Ordinals));
            var before = await first.Store().AppendAsync("orders-1", StreamState.NoStream, [Event("order_placed"), Event("order_paid")], TestContext.Current.CancellationToken);
            await NumberedAsync(first.Tail(), before.Position);

            // Act: the first instance stops, by itself numbering or not; whatever is appended
            // from then on is the second's to number.
            await first.StopAsync(TestContext.Current.CancellationToken);
            var after = await second.Store().AppendAsync("orders-2", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
            await NumberedAsync(second.Tail(), after.Position);
            var byCategory = await second.Store().ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
            var byType = await second.Store().ReadByOrdinalAsync(Placed, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);

            // Assert: one sequence across both, dense, nothing numbered twice.
            byCategory.Select(record => record.Ordinal).ShouldBe([0L, 1L, 2L]);
            byCategory.Select(record => record.Stream).ShouldBe(["orders-1", "orders-1", "orders-2"]);
            byType.Select(record => record.Ordinal).ShouldBe([0L, 1L]);
            await second.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await TestDatabases.DropAsync(name);
        }
    }

    [Fact]
    public async Task Sequencer_WhenTheInstanceThatHeldTheLeaseIsGone_ShouldTakeOverOnceTheLeaseLapses()
    {
        // Arrange: an instance that numbered and died without letting go leaves its lease
        // behind, here for a second and a half. The instance that starts asks, is refused, and
        // asks again every third of its own lease, three tenths of a second.
        static void Ordinals(NightingaleOptions options)
        {
            options.Store.AssignOrdinals = true;
            options.OrdinalSequencerLeaseDuration = TimeSpan.FromMilliseconds(900);
        }

        var name = TestDatabases.NewName();
        try
        {
            await TestDatabases.ProvisionAsync(name, Ordinals);
            var services = new ServiceCollection();
            services.AddNightingalePolecat(TestDatabases.ConnectionStringFor(name), TestAuth.Off(TestDatabases.FastBoot(Ordinals)));
            await using (var provider = services.BuildServiceProvider())
            {
                var held = await provider.GetRequiredService<ISubscriptionGroupStore>()
                    .AcquireLeaseAsync(OrdinalSequencer.LeaseName, "an-instance-that-died", null, TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);
                held.ShouldBeNull();
            }

            using var host = await TestDatabases.StartHostAsync(name, TestDatabases.FastBoot(Ordinals));

            // Act
            var appended = await host.Store().AppendAsync("orders-1", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
            var whileHeld = host.Tail().Numbered;
            await NumberedAsync(host.Tail(), appended.Position);
            var byCategory = await host.Store().ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);

            // Assert: nothing is numbered under another's lease, and everything after it lapses.
            whileHeld.ShouldBeLessThan(appended.Position);
            byCategory.Select(record => record.Ordinal).ShouldBe([0L]);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await TestDatabases.DropAsync(name);
        }
    }

    [Fact]
    public async Task Follower_OnAnInstanceThatDoesNotHoldTheLease_ShouldPublishTheSequencersProgressToItsTail()
    {
        // Arrange: a second instance over the same store; the first holds the sequencer's lease,
        // so the second numbers nothing and follows the progress rows instead.
        using var other = await TestDatabases.StartHostAsync(_name, TestDatabases.FastBoot(options => options.Store.AssignOrdinals = true));
        try
        {
            // Act: appended through the first instance, numbered by it, and seen by the second.
            var appended = await Store.AppendAsync("orders-1", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
            await NumberedAsync(appended.Position);
            var theirs = await NumberedAsync(other.Tail(), appended.Position);
            var byCategory = await other.Store().ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);

            // Assert
            theirs.ShouldBeGreaterThanOrEqualTo(appended.Position);
            byCategory.Select(record => record.Ordinal).ShouldBe([0L]);
        }
        finally
        {
            await other.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Sequencer_WhenAnEventCommitsAfterTheTailGaveItsPositionUp_ShouldNumberItWithTheNextOrdinalWithoutWaitingForAnAppend()
    {
        // Arrange: an append the database refuses inside the insert, an event type longer than its
        // column, takes position 2 and never commits it; the next append lands at 3, the tail
        // gives the gap up after the store's threshold, and the sequencer numbers past it.
        var sut = Store;
        await sut.AppendAsync("orders-1", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await Should.ThrowAsync<ValueTooLongException>(async () => await sut.AppendAsync("orders-1", StreamState.StreamRevision(0), [new EventData(Guid.NewGuid(), new string('x', 501), "{}"u8.ToArray())], TestContext.Current.CancellationToken));
        var pastTheGap = await sut.AppendAsync("orders-2", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await NumberedAsync(pastTheGap.Position);
        var before = await sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);

        // Act: the gap's transaction commits after all, played by a row at its position; nothing
        // else is appended, so no head moves, and the sequencer finds it on its quiet pass.
        await TestDatabases.ExecuteAsync(_name, "SET IDENTITY_INSERT dbo.pc_events ON; INSERT INTO dbo.pc_events (seq_id, id, stream_id, version, data, type) SELECT 2, NEWID(), 'orders-1', 2, data, type FROM dbo.pc_events WHERE seq_id = 1; SET IDENTITY_INSERT dbo.pc_events OFF;");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var after = await sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, timeout.Token);
        while (after.Count < 3)
        {
            await Task.Delay(100, timeout.Token);
            after = await sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, timeout.Token);
        }

        // Assert: numbered late, with the next ordinal of its category, never left out.
        pastTheGap.Position.ShouldBe(3, "the refused append's number is gone");
        before.Select(record => (record.Position, record.Ordinal)).ShouldBe([(1L, (long?)0), (3L, 1)]);
        after.Select(record => (record.Position, record.Ordinal)).ShouldBe([(1L, (long?)0), (3L, 1), (2L, 2)]);
    }

    private IStreamStore Store => _host!.Store();

    private static EventData Event(string type) => new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    private static EventData Correlated(string type, string correlationId) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"), new JsonObject { [MetadataKeys.CorrelationId] = correlationId });

    /// <summary>How often an index was sought and scanned, by SQL Server's own count since the database was made.</summary>
    private async Task<(long Seeks, long Scans)> IndexUseAsync(string index)
    {
        const string Sql = "SELECT ISNULL(SUM(s.{0}), 0) FROM sys.indexes i LEFT JOIN sys.dm_db_index_usage_stats s ON s.object_id = i.object_id AND s.index_id = i.index_id AND s.database_id = DB_ID() WHERE i.name = '{1}'";
        var seeks = await TestDatabases.ScalarAsync<long>(_name, string.Format(CultureInfo.InvariantCulture, Sql, "user_seeks", index));
        var scans = await TestDatabases.ScalarAsync<long>(_name, string.Format(CultureInfo.InvariantCulture, Sql, "user_scans", index));
        return (seeks, scans);
    }

    /// <summary>Waits for the sequencer to pass a position, which it does on the tail's cadence, and the tail to say so.</summary>
    private Task<long> NumberedAsync(long position) => NumberedAsync(_host!.Tail(), position);

    private static async Task<long> NumberedAsync(IStoreTail tail, long position)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await tail.WaitForNumberedAsync(position, timeout.Token);
    }
}
