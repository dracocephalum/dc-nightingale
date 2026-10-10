using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Auth;

/// <summary>
/// The rules of managing credentials, against a fake table and with no transport in sight: who
/// may make what, what a bound admin sees, and the one call any role may make.
/// </summary>
public sealed class CredentialManagerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Billing = Guid.NewGuid();
    private static readonly Guid Shipping = Guid.NewGuid();
    private static readonly NightingalePrincipal GlobalAdmin = new("admin", CredentialRole.Admin, null, "Basic");
    private static readonly NightingalePrincipal BillingAdmin = new("billing-admin", CredentialRole.Admin, Billing, "Basic");

    private readonly ICredentialStore _credentials = A.Fake<ICredentialStore>();
    private readonly PasswordHasher _hasher = new(new NightingaleOptionsBase.AuthSettings { Iterations = 10_000 });
    private readonly CredentialManager _sut;

    public CredentialManagerTests()
    {
        _sut = new CredentialManager(_credentials, _hasher, new TestOptions(), new FakeTimeProvider(Now), new Authorizer());
        A.CallTo(() => _credentials.FindAsync(A<string>._, A<CancellationToken>._)).Returns((Credential?)null);
        A.CallTo(() => _credentials.UpdateAsync(A<Guid>._, A<CredentialRole?>._, A<Guid?>._, A<bool?>._, A<string?>._, A<CancellationToken>._))
            .ReturnsLazily((Guid id, CredentialRole? _, Guid? _, bool? _, string? _, CancellationToken _) => new Credential { Id = id, Name = "updated", PasswordHash = string.Empty });
    }

    [Fact]
    public async Task CreateAsync_ShouldHashThePasswordAndStampTheRow()
    {
        // Arrange
        Credential? written = null;
        A.CallTo(() => _credentials.CreateAsync(A<Credential>._, A<CancellationToken>._)).Invokes((Credential row, CancellationToken _) => written = row);

        // Act
        var made = await _sut.CreateAsync(GlobalAdmin, new NewCredential("operator", "operator-password-12", CredentialRole.Ops, Billing), TestContext.Current.CancellationToken);

        // Assert
        written.ShouldBeSameAs(made);
        made.Name.ShouldBe("operator");
        made.Role.ShouldBe(CredentialRole.Ops);
        made.TenantId.ShouldBe(Billing);
        made.CreatedAt.ShouldBe(Now);
        made.PasswordChangedAt.ShouldBe(Now);
        _hasher.Verify("operator-password-12", made.PasswordHash).Matches.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateAsync_ShouldKeepTheRoleAndTenantWithinTheCallers()
    {
        // Act
        var notAdmin = await Should.ThrowAsync<AccessDeniedException>(async () => await _sut.CreateAsync(new NightingalePrincipal("ops", CredentialRole.Ops, null, "Basic"), new NewCredential("x", "x-password-12345", CredentialRole.User, null), TestContext.Current.CancellationToken));
        var otherTenant = await Should.ThrowAsync<AccessDeniedException>(async () => await _sut.CreateAsync(BillingAdmin, new NewCredential("x", "x-password-12345", CredentialRole.User, Shipping), TestContext.Current.CancellationToken));
        var global = await Should.ThrowAsync<AccessDeniedException>(async () => await _sut.CreateAsync(BillingAdmin, new NewCredential("x", "x-password-12345", CredentialRole.User, null), TestContext.Current.CancellationToken));
        var ownTenantAdmin = await _sut.CreateAsync(BillingAdmin, new NewCredential("billing-admin-2", "x-password-12345", CredentialRole.Admin, Billing), TestContext.Current.CancellationToken);

        // Assert
        notAdmin.Required.ShouldBe(CredentialRole.Admin);
        otherTenant.Message.ShouldContain("its own tenant");
        global.Message.ShouldContain("its own tenant");
        ownTenantAdmin.TenantId.ShouldBe(Billing);
        A.CallTo(() => _credentials.CreateAsync(ownTenantAdmin, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CreateAsync_WithABadNameOrPassword_ShouldSayWhatIsWrong()
    {
        // Act
        var admin = await Should.ThrowAsync<ArgumentException>(async () => await _sut.CreateAsync(GlobalAdmin, new NewCredential("Admin", "x-password-12345", CredentialRole.User, null), TestContext.Current.CancellationToken));
        var colon = await Should.ThrowAsync<ArgumentException>(async () => await _sut.CreateAsync(GlobalAdmin, new NewCredential("a:b", "x-password-12345", CredentialRole.User, null), TestContext.Current.CancellationToken));
        var shortPassword = await Should.ThrowAsync<ArgumentException>(async () => await _sut.CreateAsync(GlobalAdmin, new NewCredential("reader", "short", CredentialRole.User, null), TestContext.Current.CancellationToken));

        // Assert
        admin.Message.ShouldContain("built-in administrator");
        colon.Message.ShouldContain("':'");
        shortPassword.Message.ShouldContain("at least 12 characters");
    }

    [Fact]
    public async Task UpdateAsync_ForABoundAdmin_ShouldNotFindAnotherTenantsCredentialAndShouldMakeGlobalThroughTheStore()
    {
        // Arrange
        A.CallTo(() => _credentials.FindAsync("shipping-reader", A<CancellationToken>._)).Returns(new Credential { Id = Guid.NewGuid(), Name = "shipping-reader", PasswordHash = string.Empty, Role = CredentialRole.User, TenantId = Shipping });
        var own = new Credential { Id = Guid.NewGuid(), Name = "billing-reader", PasswordHash = string.Empty, Role = CredentialRole.User, TenantId = Billing };
        A.CallTo(() => _credentials.FindAsync("billing-reader", A<CancellationToken>._)).Returns(own);

        // Act
        var hidden = await Should.ThrowAsync<CredentialNotFoundException>(async () => await _sut.UpdateAsync(BillingAdmin, "shipping-reader", new CredentialChanges(Disabled: true), TestContext.Current.CancellationToken));
        var unbinding = await Should.ThrowAsync<AccessDeniedException>(async () => await _sut.UpdateAsync(BillingAdmin, "billing-reader", new CredentialChanges(Tenant: TenantBinding.Global), TestContext.Current.CancellationToken));
        await _sut.UpdateAsync(GlobalAdmin, "billing-reader", new CredentialChanges(CredentialRole.Ops, TenantBinding.Global, Disabled: false), TestContext.Current.CancellationToken);

        // Assert
        hidden.Message.ShouldContain("shipping-reader");
        unbinding.Message.ShouldContain("its own tenant");
        A.CallTo(() => _credentials.UpdateAsync(own.Id, CredentialRole.Ops, Guid.Empty, false, null, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_ShouldRefuseTheAdministratorAndAWrongCurrentPassword()
    {
        // Arrange
        var reader = new Credential { Id = Guid.NewGuid(), Name = "reader", PasswordHash = _hasher.Hash("reader-password-12"), Role = CredentialRole.User, TenantId = Billing };
        A.CallTo(() => _credentials.FindAsync("reader", A<CancellationToken>._)).Returns(reader);
        string? stored = null;
        A.CallTo(() => _credentials.UpdateAsync(reader.Id, null, null, null, A<string?>._, A<CancellationToken>._))
            .Invokes((Guid _, CredentialRole? _, Guid? _, bool? _, string? hash, CancellationToken _) => stored = hash);

        // Act
        var administrator = await Should.ThrowAsync<AccessDeniedException>(async () => await _sut.ChangeOwnPasswordAsync(NightingalePrincipal.Anonymous, "x", "y-password-123456", TestContext.Current.CancellationToken));
        var wrong = await Should.ThrowAsync<AuthenticationFailedException>(async () => await _sut.ChangeOwnPasswordAsync(new NightingalePrincipal("reader", CredentialRole.User, Billing, "Basic"), "reader-password-13", "reader-password-14", TestContext.Current.CancellationToken));
        await _sut.ChangeOwnPasswordAsync(new NightingalePrincipal("reader", CredentialRole.User, Billing, "Basic"), "reader-password-12", "reader-password-14", TestContext.Current.CancellationToken);

        // Assert
        administrator.Message.ShouldContain("configuration");
        wrong.Message.ShouldContain("current password");
        stored.ShouldNotBeNull();
        _hasher.Verify("reader-password-14", stored).Matches.ShouldBeTrue();
    }

    private sealed class TestOptions : NightingaleOptionsBase
    {
    }
}
