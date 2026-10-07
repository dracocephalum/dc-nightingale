using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The virtual streams under ordinal numbering, against a store initialized with ordinals: the
/// sequencer runs in the host exactly as it would in production, behind the tail, on its lease,
/// and the tests wait for its progress rather than numbering anything themselves. Every test uses
/// a database of its own, dropped at the end whatever happened, one at a time.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class OrdinalTests : IAsyncLifetime
{
    private static readonly VirtualStreamName Orders = new(VirtualStreamKind.Category, "orders");
    private static readonly VirtualStreamName Placed = new(VirtualStreamKind.EventType, "order_placed");
    private readonly string _name = TestDatabases.NewName();
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        _host = await TestDatabases.StartProvisionedHostAsync(_name, options =>
        {
            options.Store.AssignOrdinals = true;
            options.Deletion.AllowDelete = true;
            options.Deletion.AllowTombstone = true;
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
        await sut.DeleteAsync("orders-2", StreamState.Any, TestContext.Current.CancellationToken);

        // Act
        var withHole = await sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var fourth = await sut.AppendAsync("orders-4", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
        await NumberedAsync(fourth.Position);
        var afterFourth = await sut.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        await sut.TombstoneAsync("orders-1", StreamState.Any, TestContext.Current.CancellationToken);
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
            options.SequencerLeaseDuration = TimeSpan.FromMilliseconds(900);
        }

        var name = TestDatabases.NewName();
        try
        {
            using var first = await TestDatabases.StartProvisionedHostAsync(name, Ordinals);
            using var second = await TestDatabases.StartHostAsync(name, TestDatabases.FastBoot(Ordinals));
            var before = await first.Store().AppendAsync("orders-1", StreamState.NoStream, [Event("order_placed"), Event("order_paid")], TestContext.Current.CancellationToken);
            await NumberedAsync(first.Store(), before.Position);

            // Act: the first instance stops, by itself numbering or not; whatever is appended
            // from then on is the second's to number.
            await first.StopAsync(TestContext.Current.CancellationToken);
            var after = await second.Store().AppendAsync("orders-2", StreamState.NoStream, [Event("order_placed")], TestContext.Current.CancellationToken);
            await NumberedAsync(second.Store(), after.Position);
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
            options.SequencerLeaseDuration = TimeSpan.FromMilliseconds(900);
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
            var whileHeld = await host.Store().NumberedThroughAsync(TestContext.Current.CancellationToken);
            await NumberedAsync(host.Store(), appended.Position);
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

    private IStreamStore Store => _host!.Store();

    private static EventData Event(string type) => new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    /// <summary>Waits for the sequencer to pass a position, which it does on the tail's cadence.</summary>
    private Task NumberedAsync(long position) => NumberedAsync(Store, position);

    private static async Task NumberedAsync(IStreamStore store, long position)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (await store.NumberedThroughAsync(timeout.Token) < position)
        {
            await Task.Delay(100, timeout.Token);
        }
    }
}
