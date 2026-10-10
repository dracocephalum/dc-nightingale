using Dracocephalum.Nightingale.Server.Auth;
using FakeItEasy;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>The stores of a host that keeps one tenant: every scope gets the one store, and the wildcard gets no groups.</summary>
public sealed class TenantStoresTests
{
    private readonly IStreamStore _streams = A.Fake<IStreamStore>();
    private readonly ISubscriptionGroupStore _groups = A.Fake<ISubscriptionGroupStore>();

    [Fact]
    public void GetStreams_ShouldHandEveryScopeTheOneStore()
    {
        // Arrange
        var sut = new TenantStores(_streams, _groups);

        // Act & Assert
        sut.GetStreams(TenantScope.Default).ShouldBeSameAs(_streams);
        sut.GetStreams(TenantScope.OfStoreTenant("7")).ShouldBeSameAs(_streams);
        sut.GetStreams(TenantScope.Every).ShouldBeSameAs(_streams);
        sut.GetGroups(TenantScope.OfStoreTenant("7")).ShouldBeSameAs(_groups);
        sut.SupportsEveryTenant.ShouldBeTrue();
    }

    [Fact]
    public void GetGroups_UnderTheWildcardOrWithNoGroupStore_ShouldRefuse()
    {
        // Arrange
        var sut = new TenantStores(_streams, _groups);
        var without = new TenantStores(_streams);

        // Act & Assert
        Should.Throw<ArgumentException>(() => sut.GetGroups(TenantScope.Every)).Message.ShouldContain("wildcard");
        Should.Throw<InvalidOperationException>(() => without.GetGroups(TenantScope.Default));
    }
}
