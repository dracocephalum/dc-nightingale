using Dracocephalum.Nightingale.Server.Auth;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Auth;

/// <summary>
/// The decisions that need no tenant directory: the role, and a bound credential kept to its
/// own tenant. The decisions that resolve a tenant are covered through a host in
/// <see cref="AuthorizationTests"/>.
/// </summary>
public sealed class AuthorizerTests
{
    private static readonly Guid Own = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Fact]
    public async Task AuthorizeAsync_WhenTheRoleIsTooLow_ShouldRefuseAndSayWhichRoleItNeeds()
    {
        // Arrange
        var sut = new Authorizer();
        var user = new NightingalePrincipal("reader", CredentialRole.User, Own, "Basic");

        // Act
        var refused = await Should.ThrowAsync<AccessDeniedException>(async () => await sut.AuthorizeAsync(user, CredentialRole.Ops, TenantAccess.Write, TenantSelection.None, TestContext.Current.CancellationToken));
        var required = await Should.ThrowAsync<AccessDeniedException>(async () => await sut.RequireRoleAsync(user, CredentialRole.Admin, TestContext.Current.CancellationToken));

        // Assert
        refused.Required.ShouldBe(CredentialRole.Ops);
        required.Required.ShouldBe(CredentialRole.Admin);
    }

    [Fact]
    public async Task AuthorizeAsync_ForABoundCredential_ShouldRefuseAnotherTenantWithoutRevealingIt()
    {
        // Arrange
        var sut = new Authorizer();
        var bound = new NightingalePrincipal("reader", CredentialRole.User, Own, "Basic");

        // Act
        var own = await sut.AuthorizeAsync(bound, CredentialRole.User, TenantAccess.Read, new TenantSelection(Own, false), TestContext.Current.CancellationToken);
        var none = await sut.AuthorizeAsync(bound, CredentialRole.User, TenantAccess.Read, TenantSelection.None, TestContext.Current.CancellationToken);
        var other = await Should.ThrowAsync<TenantNotFoundException>(async () => await sut.AuthorizeAsync(bound, CredentialRole.User, TenantAccess.Read, new TenantSelection(Other, false), TestContext.Current.CancellationToken));
        var wildcard = await Should.ThrowAsync<TenantNotFoundException>(async () => await sut.AuthorizeAsync(bound, CredentialRole.User, TenantAccess.Read, TenantSelection.Wildcard, TestContext.Current.CancellationToken));

        // Assert
        own.ShouldBe(TenantScope.Default);
        none.ShouldBe(TenantScope.Default);
        other.TenantId.ShouldBe(Other);
        wildcard.TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutADirectory_ShouldGiveTheInstancesOneTenantToAnyGlobalCall()
    {
        // Arrange
        var sut = new Authorizer();

        // Act
        var anonymous = await sut.AuthorizeAsync(NightingalePrincipal.Anonymous, CredentialRole.Admin, TenantAccess.Write, TenantSelection.None, TestContext.Current.CancellationToken);
        var wildcard = await sut.AuthorizeAsync(NightingalePrincipal.Anonymous, CredentialRole.Admin, TenantAccess.Write, TenantSelection.Wildcard, TestContext.Current.CancellationToken);

        // Assert
        anonymous.ShouldBe(TenantScope.Default);
        wildcard.ShouldBe(TenantScope.Default);
    }
}
