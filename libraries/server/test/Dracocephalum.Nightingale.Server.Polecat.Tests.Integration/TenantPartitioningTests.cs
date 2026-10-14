using System.Text;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Server.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// A store partitioned by tenant, with ordinals: each tenant has a sequence of its own, so
/// positions restart per tenant, a tail of its own, which the server polls itself, and a
/// sequencer progress of its own. A gap in one tenant holds that tenant alone and is given up
/// after the store's threshold, on the real table. Every test uses a database of its own.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantPartitioningTests : IAsyncLifetime
{
    private static readonly VirtualStreamName Orders = new(VirtualStreamKind.Category, "orders");
    private readonly string _name = TestDatabases.NewName();
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        _host = await TestDatabases.StartProvisionedHostAsync(_name, options =>
        {
            options.Store.Partitioning = PartitioningMode.Tenant;
            options.Store.AssignOrdinals = true;
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
    public async Task Tails_ShouldFollowEachTenantsOwnMarkOverPositionsThatRestartPerTenant()
    {
        // Arrange
        var stores = Stores;
        var acme = await CreateTenantAsync("acme");
        var defaults = stores.GetStreams(TenantScope.Default);
        var acmes = stores.GetStreams(acme);

        // Act
        var first = await defaults.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        var second = await acmes.AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var defaultHead = await ReachAsync(stores.GetTail(TenantScope.Default), first.Position);
        var acmeHead = await ReachAsync(stores.GetTail(acme), second.Position);
        var acmeAll = await acmes.ReadAllAsync(Direction.Forwards, 1, acmeHead, 10, TestContext.Current.CancellationToken);
        var wildcard = Should.Throw<ArgumentException>(() => stores.GetTail(TenantScope.AllTenants));

        // Assert: both tenants start at 1; each tail stops at its own tenant's last event.
        first.Position.ShouldBe(2);
        second.Position.ShouldBe(1);
        defaultHead.ShouldBe(2);
        acmeHead.ShouldBe(1);
        acmeAll.Select(record => record.Position).ShouldBe([1L]);
        acmeAll.Single().Stream.ShouldBe("orders-1");
        stores.SupportsAllTenants.ShouldBeFalse();
        wildcard.Message.ShouldContain("each tenant on its own");
        stores.GetTail(acme).ShouldBeSameAs(stores.GetTail(acme));
        stores.GetTail(acme).ShouldNotBeSameAs(stores.GetTail(TenantScope.Default));
    }

    [Fact]
    public async Task Tails_WhenARefusedAppendLeavesAGapInOneTenant_ShouldLetTheOtherAdvanceAndGiveTheGapUpAfterTheThreshold()
    {
        // Arrange: an append the database refuses inside the insert, an event type longer than
        // its column, has taken a number of the default tenant's sequence and never commits it,
        // so the next append lands one past the gap. A stale expected revision would not do: the
        // store refuses that before taking a number.
        var stores = Stores;
        var acme = await CreateTenantAsync("acme");
        var defaults = stores.GetStreams(TenantScope.Default);
        var acmes = stores.GetStreams(acme);
        await defaults.AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await Should.ThrowAsync<ValueTooLongException>(async () => await defaults.AppendAsync("orders-1", StreamState.StreamRevision(0), [new EventData(Guid.NewGuid(), new string('x', 501), "{}"u8.ToArray())], TestContext.Current.CancellationToken));

        // Act
        var pastTheGap = await defaults.AppendAsync("orders-1", StreamState.StreamRevision(0), [Event()], TestContext.Current.CancellationToken);
        var theirs = await acmes.AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var acmeHead = await ReachAsync(stores.GetTail(acme), theirs.Position, TimeSpan.FromSeconds(5));
        var defaultHead = await ReachAsync(stores.GetTail(TenantScope.Default), pastTheGap.Position, PolecatTenantTails.StaleAfter + TimeSpan.FromSeconds(10));

        // Assert
        pastTheGap.Position.ShouldBe(3, "the refused append's number is gone");
        acmeHead.ShouldBe(1);
        defaultHead.ShouldBe(3);
    }

    [Fact]
    public async Task Sequencer_ShouldNumberEachTenantsCategoryDenselyBehindItsOwnMark()
    {
        // Arrange
        var stores = Stores;
        var acme = await CreateTenantAsync("acme");
        var defaults = stores.GetStreams(TenantScope.Default);
        var acmes = stores.GetStreams(acme);
        var mine = await defaults.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        var theirs = await acmes.AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await NumberedAsync(stores.GetTail(TenantScope.Default), mine.Position);
        await NumberedAsync(stores.GetTail(acme), theirs.Position);

        // Act
        var mineByOrdinal = await defaults.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var theirsByOrdinal = await acmes.ReadByOrdinalAsync(Orders, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);

        // Assert: dense from zero in each tenant, over each tenant's own positions.
        mineByOrdinal.Select(record => record.Ordinal).ShouldBe([0L, 1L]);
        mineByOrdinal.Select(record => record.Position).ShouldBe([1L, 2L]);
        theirsByOrdinal.Select(record => record.Ordinal).ShouldBe([0L]);
        theirsByOrdinal.Select(record => record.Position).ShouldBe([1L]);
        (await acmes.OrdinalHeadAsync(Orders, TestContext.Current.CancellationToken)).ShouldBe(new StreamHead(0, 0));
    }

    [Fact]
    public async Task Sequencer_ShouldLinkEachTenantsCorrelationToItsOwnEventsAtPositionsTheTenantsShare()
    {
        // Arrange: the same workflow id in two tenants, each beginning its own stream at position
        // 1 of its own sequence; a link names its event by tenant and position, and the read
        // fetches it in the tenant's partition.
        var stores = Stores;
        var acme = await CreateTenantAsync("acme");
        var defaults = stores.GetStreams(TenantScope.Default);
        var acmes = stores.GetStreams(acme);
        var workflow = new VirtualStreamName(VirtualStreamKind.Correlation, "checkout-1");
        var mine = await defaults.AppendAsync("orders-1", StreamState.NoStream, [Correlated("checkout-1"), Correlated("checkout-1")], TestContext.Current.CancellationToken);
        var theirs = await acmes.AppendAsync("payments-1", StreamState.NoStream, [Correlated("checkout-1")], TestContext.Current.CancellationToken);
        await NumberedAsync(stores.GetTail(TenantScope.Default), mine.Position);
        await NumberedAsync(stores.GetTail(acme), theirs.Position);

        // Act
        var mineByOrdinal = await defaults.ReadByOrdinalAsync(workflow, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var theirsByOrdinal = await acmes.ReadByOrdinalAsync(workflow, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var theirListing = await acmes.ReadByOrdinalAsync(new VirtualStreamName(VirtualStreamKind.Streams, string.Empty), Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);

        // Assert: dense from zero in each tenant, each read its own tenant's events.
        mineByOrdinal.Select(record => (record.Stream, record.Position, record.Ordinal)).ShouldBe([("orders-1", 1L, (long?)0), ("orders-1", 2L, 1)]);
        theirsByOrdinal.Select(record => (record.Stream, record.Position, record.Ordinal)).ShouldBe([("payments-1", 1L, (long?)0)]);
        theirListing.Select(record => record.Stream).ShouldBe(["payments-1"]);
    }

    private ITenantStores Stores => _host!.Services.GetRequiredService<ITenantStores>();

    private static EventData Correlated(string correlationId) =>
        new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{\"orderId\":1}"), new JsonObject { [MetadataKeys.CorrelationId] = correlationId });

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    private async Task<TenantScope> CreateTenantAsync(string name)
    {
        var tenant = await _host!.Services.GetRequiredService<ITenantStore>().CreateAsync(name, TestContext.Current.CancellationToken);
        return TenantScope.OfStoreTenant(tenant.StoreTenantId);
    }

    /// <summary>The head once it has reached a position every event up to which is committed, within a time or not at all.</summary>
    private static async Task<long> ReachAsync(IStoreTail tail, long position, TimeSpan? within = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(within ?? TimeSpan.FromSeconds(10));
        var head = await tail.RefreshAsync(timeout.Token);
        while (head < position)
        {
            head = await tail.WaitForAdvanceAsync(head, timeout.Token);
        }

        return head;
    }

    /// <summary>Waits for the sequencer to pass a position of a tenant's sequence, which it does on the tails' cadence, and the tail to say so.</summary>
    private static async Task NumberedAsync(IStoreTail tail, long position)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await tail.WaitForNumberedAsync(position, timeout.Token);
    }
}
