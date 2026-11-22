using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Polecat.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using FakeItEasy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Polecat;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// Which store, group store and tail a scope gets, over a document store that is handed on and
/// never called: one of each per tenant, kept, and what the wildcard may and may not have.
/// </summary>
public sealed class PolecatTenantStoresTests
{
    private static readonly TenantScope Billing = new(Guid.NewGuid(), "7");
    private static readonly TenantScope Shipping = new(Guid.NewGuid(), "8");

    private readonly Dictionary<string, IStoreTail> _tails = new(StringComparer.Ordinal);

    [Fact]
    public void GetStreams_ShouldMakeOneStoreATenantAndKeepIt()
    {
        // Arrange
        var sut = Stores(supportsAllTenants: true);

        // Act
        var billing = sut.GetStreams(Billing);
        var again = sut.GetStreams(Billing);
        var shipping = sut.GetStreams(Shipping);
        var noTenant = sut.GetStreams(TenantScope.Default);
        var allTenants = sut.GetStreams(TenantScope.AllTenants);

        // Assert
        again.ShouldBeSameAs(billing);
        new[] { billing, shipping, noTenant, allTenants }.Distinct().Count().ShouldBe(4);
        sut.GetStreams(TenantScope.AllTenants).ShouldBeSameAs(allTenants);
    }

    [Fact]
    public void GetGroups_ShouldMakeOneGroupStoreATenantAndKeepIt()
    {
        // Arrange
        var sut = Stores(supportsAllTenants: true);

        // Act
        var billing = sut.GetGroups(Billing);
        var again = sut.GetGroups(Billing);
        var shipping = sut.GetGroups(Shipping);

        // Assert
        again.ShouldBeSameAs(billing);
        shipping.ShouldNotBeSameAs(billing);
    }

    [Fact]
    public void GetGroups_UnderTheWildcard_ShouldRefuse()
    {
        // Arrange
        var sut = Stores(supportsAllTenants: true);

        // Act
        var refusal = Should.Throw<ArgumentException>(() => sut.GetGroups(TenantScope.AllTenants));

        // Assert
        refusal.Message.ShouldContain("Persistent subscriptions are per tenant");
    }

    [Fact]
    public void GetTail_ShouldAskForTheTailOfTheScopesTenantByTheIdTheStoreKeeps()
    {
        // Arrange
        var sut = Stores(supportsAllTenants: true);

        // Act
        var billing = sut.GetTail(Billing);
        var noTenant = sut.GetTail(TenantScope.Default);
        var allTenants = sut.GetTail(TenantScope.AllTenants);

        // Assert: a scope that names no tenant is the store's default tenant, the wildcard included.
        billing.ShouldBeSameAs(_tails["7"]);
        noTenant.ShouldBeSameAs(_tails[JasperFx.StorageConstants.DefaultTenantId]);
        allTenants.ShouldBeSameAs(noTenant);
    }

    [Fact]
    public void TheWildcard_WhenEachTenantIsNumberedOnItsOwn_ShouldHaveNeitherAStoreNorATail()
    {
        // Arrange
        var sut = Stores(supportsAllTenants: false);

        // Act
        var streams = Should.Throw<ArgumentException>(() => sut.GetStreams(TenantScope.AllTenants));
        var tail = Should.Throw<ArgumentException>(() => sut.GetTail(TenantScope.AllTenants));

        // Assert: a tenant's own store and tail are there all the same.
        sut.SupportsAllTenants.ShouldBeFalse();
        streams.Message.ShouldContain("no read spans all tenants");
        tail.Message.ShouldContain("no one head spans all tenants");
        sut.GetStreams(Billing).ShouldNotBeNull();
        sut.GetTail(Billing).ShouldNotBeNull();
    }

    [Fact]
    public void NameComparer_ShouldBeTheOneTheDatabasesCollationGave()
    {
        // Arrange
        var names = new NameComparison();
        var sut = Stores(supportsAllTenants: true, names);

        // Act
        var before = sut.NameComparer.Equals("Orders-1", "orders-1");
        names.Adopt(new DatabaseCollation("Latin1_General_100_CI_AS_SC_UTF8", 65001, 1, 1033));
        var after = sut.NameComparer.Equals("Orders-1", "orders-1");

        // Assert
        before.ShouldBeFalse();
        after.ShouldBeTrue();
    }

    private PolecatTenantStores Stores(bool supportsAllTenants, NameComparison? names = null) =>
        new(
            A.Fake<IDocumentStore>(),
            new NoContexts(),
            ordinals: false,
            partitionedByTenant: false,
            readOnlyEvents: null,
            readOnlyStreams: null,
            new NoGatewayContexts(),
            supportsAllTenants,
            tenant => _tails.TryGetValue(tenant, out var tail) ? tail : _tails[tenant] = A.Fake<IStoreTail>(),
            new AppendSignal(TimeProvider.System),
            names ?? new NameComparison(),
            TimeProvider.System,
            NullLogger<PolecatStreamStore>.Instance);

    /// <summary>A factory the stores hand on and never call here.</summary>
    private sealed class NoContexts : IDbContextFactory<EventsDbContext>
    {
        public EventsDbContext CreateDbContext() => throw new InvalidOperationException("Choosing a store reads nothing.");
    }

    /// <summary>A factory the group stores hand on and never call here.</summary>
    private sealed class NoGatewayContexts : IDbContextFactory<NightingaleDbContext>
    {
        public NightingaleDbContext CreateDbContext() => throw new InvalidOperationException("Choosing a group store reads nothing.");
    }
}
