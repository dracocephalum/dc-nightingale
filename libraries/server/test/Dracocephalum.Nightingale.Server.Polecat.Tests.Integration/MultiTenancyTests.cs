using Dracocephalum.Nightingale.Server.Auth;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// Tenants over one database and one sequence: a stream name, a category, <c>$all</c> and a
/// group are each the tenant's own, and only the wildcard store sees across tenants. The shared
/// database holds other tests' events, so every name here carries a suffix of its own, and the
/// tenants are ids no other test uses.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class MultiTenancyTests(SqlServerTestDatabase database)
{
    [Fact]
    public async Task Streams_ShouldBeTheTenantsOwnAndInvisibleToEveryOther()
    {
        // Arrange
        var (stores, acme, globex) = Tenants();
        var stream = "orders-" + Guid.NewGuid().ToString("N");
        var placed = "placed_" + Guid.NewGuid().ToString("N");

        // Act
        var appended = await stores.GetStreams(acme).AppendAsync(stream, StreamState.NoStream, [Event(placed)], TestContext.Current.CancellationToken);
        var own = await stores.GetStreams(acme).ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var other = await stores.GetStreams(globex).ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var defaults = await stores.GetStreams(TenantScope.Default).ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var otherAppend = await stores.GetStreams(globex).AppendAsync(stream, StreamState.NoStream, [Event(placed), Event(placed)], TestContext.Current.CancellationToken);

        // Assert: the same name is a different stream in each tenant, each with its own revisions.
        appended.Revision.ShouldBe(0);
        own.ShouldNotBeNull().Events.Select(record => record.Type).ShouldBe([placed]);
        other.ShouldBeNull();
        defaults.ShouldBeNull();
        otherAppend.Revision.ShouldBe(1);
        (await stores.GetStreams(acme).ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Head.Last.ShouldBe(0);
    }

    [Fact]
    public async Task AllAndTheVirtualStreams_ShouldBePerTenantAndSpanTenantsOnlyUnderTheWildcard()
    {
        // Arrange
        var (stores, acme, globex) = Tenants();
        var category = "cat" + Guid.NewGuid().ToString("N");
        var placed = "placed_" + Guid.NewGuid().ToString("N");
        var a = await stores.GetStreams(acme).AppendAsync(category + "-1", StreamState.NoStream, [Event(placed)], TestContext.Current.CancellationToken);
        var b = await stores.GetStreams(globex).AppendAsync(category + "-1", StreamState.NoStream, [Event(placed)], TestContext.Current.CancellationToken);
        var head = await ReachAsync(b.Position);
        var virtualStream = new VirtualStreamName(VirtualStreamKind.Category, category);

        // Act
        var acmeAll = await stores.GetStreams(acme).ReadAllAsync(Direction.Forwards, a.Position, head, 100, TestContext.Current.CancellationToken);
        var everyAll = await stores.GetStreams(TenantScope.Every).ReadAllAsync(Direction.Forwards, a.Position, head, 100, TestContext.Current.CancellationToken);
        var acmeBounds = await stores.GetStreams(acme).VirtualHeadAsync(virtualStream, head, TestContext.Current.CancellationToken);
        var globexBounds = await stores.GetStreams(globex).VirtualHeadAsync(virtualStream, head, TestContext.Current.CancellationToken);
        var everyBounds = await stores.GetStreams(TenantScope.Every).VirtualHeadAsync(virtualStream, head, TestContext.Current.CancellationToken);
        var everyStream = await Should.ThrowAsync<ArgumentException>(async () => await stores.GetStreams(TenantScope.Every).ReadAsync(category + "-1", Direction.Forwards, null, 1, TestContext.Current.CancellationToken));

        // Assert
        acmeAll.Select(record => record.Position).ShouldNotContain(b.Position);
        acmeAll.Select(record => record.Position).ShouldContain(a.Position);
        everyAll.Select(record => record.Position).ShouldContain(a.Position);
        everyAll.Select(record => record.Position).ShouldContain(b.Position);
        acmeBounds.ShouldBe(new StreamHead(a.Position, a.Position));
        globexBounds.ShouldBe(new StreamHead(b.Position, b.Position));
        everyBounds.ShouldBe(new StreamHead(a.Position, b.Position));
        everyStream.Message.ShouldContain("wildcard");
    }

    [Fact]
    public async Task Groups_ShouldBeTheTenantsOwnUnderTheSameStreamAndName()
    {
        // Arrange
        var (stores, acme, globex) = Tenants();
        var stream = "orders-" + Guid.NewGuid().ToString("N");
        var group = "billing-" + Guid.NewGuid().ToString("N");

        // Act
        await stores.GetGroups(acme).CreateAsync(new SubscriptionGroupDefinition(stream, group, GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await stores.GetGroups(globex).CreateAsync(new SubscriptionGroupDefinition(stream, group, GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        var acmeGroup = await stores.GetGroups(acme).GetAsync(stream, group, TestContext.Current.CancellationToken);
        var globexGroup = await stores.GetGroups(globex).GetAsync(stream, group, TestContext.Current.CancellationToken);
        var defaultGroup = await stores.GetGroups(TenantScope.Default).GetAsync(stream, group, TestContext.Current.CancellationToken);
        var wildcard = Should.Throw<ArgumentException>(() => stores.GetGroups(TenantScope.Every));

        // Assert: two groups, one per tenant, each carrying its tenant for its runtime to resolve.
        acmeGroup.ShouldNotBeNull().TenantId.ShouldBe(acme.StoreTenantId);
        globexGroup.ShouldNotBeNull().TenantId.ShouldBe(globex.StoreTenantId);
        acmeGroup.Id.ShouldNotBe(globexGroup.Id);
        defaultGroup.ShouldBeNull();
        wildcard.Message.ShouldContain("wildcard");
        stores.SupportsEveryTenant.ShouldBeTrue();
    }

    private static EventData Event(string type) => new(Guid.NewGuid(), type, "{}"u8.ToArray(), null);

    private (ITenantStores Stores, TenantScope Acme, TenantScope Globex) Tenants()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (database.Services.GetRequiredService<ITenantStores>(), TenantScope.OfStoreTenant("acme-" + suffix), TenantScope.OfStoreTenant("globex-" + suffix));
    }

    /// <summary>The head once it has reached a position every event up to which is committed.</summary>
    private async Task<long> ReachAsync(long position)
    {
        var tail = database.Services.GetRequiredService<IStoreTail>();
        var head = await tail.RefreshAsync(TestContext.Current.CancellationToken);
        while (head < position)
        {
            head = await tail.WaitForAdvanceAsync(head, TestContext.Current.CancellationToken);
        }

        return head;
    }
}
