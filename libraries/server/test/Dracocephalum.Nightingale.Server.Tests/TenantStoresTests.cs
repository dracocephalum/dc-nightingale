using Dracocephalum.Nightingale.Server.Auth;
using FakeItEasy;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>The stores of a host that keeps one tenant: every scope gets the one store and the one tail, and the wildcard gets no groups.</summary>
public sealed class TenantStoresTests
{
    private readonly IStreamStore _streams = A.Fake<IStreamStore>();
    private readonly ISubscriptionGroupStore _groups = A.Fake<ISubscriptionGroupStore>();
    private readonly IStoreTail _tail = A.Fake<IStoreTail>();

    [Fact]
    public void GetStreams_ShouldHandEveryScopeTheOneStore()
    {
        // Arrange
        var sut = new TenantStores(_streams, _groups, _tail);

        // Act & Assert
        sut.GetStreams(TenantScope.Default).ShouldBeSameAs(_streams);
        sut.GetStreams(TenantScope.OfStoreTenant("7")).ShouldBeSameAs(_streams);
        sut.GetStreams(TenantScope.AllTenants).ShouldBeSameAs(_streams);
        sut.GetGroups(TenantScope.OfStoreTenant("7")).ShouldBeSameAs(_groups);
        sut.SupportsAllTenants.ShouldBeTrue();
    }

    [Fact]
    public void GetTail_ShouldHandEveryScopeTheOneTail()
    {
        // Arrange
        var sut = new TenantStores(_streams, _groups, _tail);

        // Act & Assert
        sut.GetTail(TenantScope.Default).ShouldBeSameAs(_tail);
        sut.GetTail(TenantScope.OfStoreTenant("7")).ShouldBeSameAs(_tail);
        sut.GetTail(TenantScope.AllTenants).ShouldBeSameAs(_tail);
    }

    [Fact]
    public void GetGroupsOrGetTail_UnderTheWildcardOrWithoutOne_ShouldRefuse()
    {
        // Arrange
        var sut = new TenantStores(_streams, _groups, _tail);
        var without = new TenantStores(_streams);

        // Act & Assert
        Should.Throw<ArgumentException>(() => sut.GetGroups(TenantScope.AllTenants)).Message.ShouldContain("wildcard");
        Should.Throw<InvalidOperationException>(() => without.GetGroups(TenantScope.Default));
        Should.Throw<InvalidOperationException>(() => without.GetTail(TenantScope.Default));
    }
}
