using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using FakeItEasy;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Auth;

/// <summary>The rules of managing tenants, against a fake table: a global admin's call, every one of them.</summary>
public sealed class TenantManagerTests
{
    private static readonly NightingalePrincipal GlobalAdmin = new("admin", CredentialRole.Admin, null, "Basic");

    private readonly ITenantStore _tenants = A.Fake<ITenantStore>();
    private readonly TenantManager _sut;

    public TenantManagerTests()
    {
        _sut = new TenantManager(_tenants, new Authorizer());
    }

    [Fact]
    public async Task CreateAsync_ShouldNeedAGlobalAdmin()
    {
        // Arrange
        A.CallTo(() => _tenants.CreateAsync("acme", A<CancellationToken>._)).Returns(new Tenant { Id = Guid.NewGuid(), Name = "acme", StoreTenantId = "2" });

        // Act
        var made = await _sut.CreateAsync(GlobalAdmin, "acme", TestContext.Current.CancellationToken);
        var ops = await Should.ThrowAsync<AccessDeniedException>(async () => await _sut.CreateAsync(new NightingalePrincipal("ops", CredentialRole.Ops, null, "Basic"), "x", TestContext.Current.CancellationToken));
        var bound = await Should.ThrowAsync<AccessDeniedException>(async () => await _sut.CreateAsync(new NightingalePrincipal("billing-admin", CredentialRole.Admin, Guid.NewGuid(), "Basic"), "x", TestContext.Current.CancellationToken));

        // Assert
        made.Name.ShouldBe("acme");
        ops.Required.ShouldBe(CredentialRole.Admin);
        bound.Message.ShouldContain("global administrator");
        A.CallTo(() => _tenants.CreateAsync(A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task UpdateAsync_WhenTheTenantIsUnknownOrTheNameIsBad_ShouldSaySo()
    {
        // Arrange
        var unknown = Guid.NewGuid();
        A.CallTo(() => _tenants.UpdateAsync(unknown, A<string?>._, A<bool?>._, A<CancellationToken>._)).Returns((Tenant?)null);

        // Act
        var notFound = await Should.ThrowAsync<TenantNotFoundException>(async () => await _sut.UpdateAsync(GlobalAdmin, unknown, null, true, TestContext.Current.CancellationToken));
        var blank = await Should.ThrowAsync<ArgumentException>(async () => await _sut.UpdateAsync(GlobalAdmin, unknown, " ", null, TestContext.Current.CancellationToken));

        // Assert
        notFound.TenantId.ShouldBe(unknown);
        blank.Message.ShouldContain("1 to 250");
    }
}
