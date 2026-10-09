using System.Text;

using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Auth;

/// <summary>
/// The Basic handler against a fake credentials table and a clock moved by hand: the built-in
/// administrator, stored credentials, what is remembered and for how long, and the lockout.
/// </summary>
public sealed class BasicAuthenticatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private readonly NightingaleOptionsBase.AuthSettings _settings = new() { AdminPassword = "admin-password-12", Iterations = 10_000, LockoutThreshold = 3, LockoutDuration = TimeSpan.FromMinutes(15), VerificationCacheDuration = TimeSpan.FromMinutes(5) };
    private readonly ICredentialStore _credentials = A.Fake<ICredentialStore>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly PasswordHasher _hasher;

    public BasicAuthenticatorTests()
    {
        _hasher = new PasswordHasher(_settings);
        A.CallTo(() => _credentials.FindAsync(A<string>._, A<CancellationToken>._)).Returns((Credential?)null);
        A.CallTo(() => _credentials.FindAsync("billing-reader", A<CancellationToken>._)).Returns(new Credential
        {
            Id = Guid.NewGuid(),
            Name = "billing-reader",
            PasswordHash = _hasher.Hash("reader-password-12"),
            Role = CredentialRole.User,
            TenantId = Tenant,
        });
    }

    [Fact]
    public async Task AuthenticateAsync_WithTheAdministratorsPassword_ShouldGiveAGlobalAdmin()
    {
        // Arrange
        var sut = Authenticator();

        // Act
        var principal = await sut.AuthenticateAsync(Basic("admin", "admin-password-12"), "peer", TestContext.Current.CancellationToken);
        var wrong = await sut.AuthenticateAsync(Basic("admin", "admin-password-13"), "peer", TestContext.Current.CancellationToken);

        // Assert
        principal.ShouldBe(new NightingalePrincipal("admin", CredentialRole.Admin, null, "Basic"));
        wrong.ShouldBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_WithAStoredCredential_ShouldGiveItsRoleAndTenantAndRememberIt()
    {
        // Arrange
        var sut = Authenticator();

        // Act
        var first = await sut.AuthenticateAsync(Basic("billing-reader", "reader-password-12"), "peer", TestContext.Current.CancellationToken);
        var second = await sut.AuthenticateAsync(Basic("billing-reader", "reader-password-12"), "peer", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(6));
        var third = await sut.AuthenticateAsync(Basic("billing-reader", "reader-password-12"), "peer", TestContext.Current.CancellationToken);

        // Assert: one lookup for the first two, another once the memory lapsed.
        first.ShouldBe(new NightingalePrincipal("billing-reader", CredentialRole.User, Tenant, "Basic"));
        second.ShouldBe(first);
        third.ShouldBe(first);
        A.CallTo(() => _credentials.FindAsync("billing-reader", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task AuthenticateAsync_WithWrongPasswords_ShouldCountThemAndRefuseAnUnknownNameTheSameWay()
    {
        // Arrange
        var sut = Authenticator();

        // Act
        var wrong = await sut.AuthenticateAsync(Basic("billing-reader", "nope"), "peer", TestContext.Current.CancellationToken);
        var unknown = await sut.AuthenticateAsync(Basic("nobody", "nope"), "peer", TestContext.Current.CancellationToken);
        var malformed = await sut.AuthenticateAsync("not base64", "peer", TestContext.Current.CancellationToken);

        // Assert
        wrong.ShouldBeNull();
        unknown.ShouldBeNull();
        malformed.ShouldBeNull();
        A.CallTo(() => _credentials.RecordFailureAsync(A<Guid>._, 3, TimeSpan.FromMinutes(15), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTheCredentialIsLockedOutOrDisabled_ShouldRefuseTheRightPassword()
    {
        // Arrange
        var locked = new Credential { Id = Guid.NewGuid(), Name = "locked", PasswordHash = _hasher.Hash("locked-password-12"), Role = CredentialRole.Ops, LockedUntil = Now.AddMinutes(1) };
        var disabled = new Credential { Id = Guid.NewGuid(), Name = "disabled", PasswordHash = _hasher.Hash("disabled-password-12"), Role = CredentialRole.Ops, IsDisabled = true };
        A.CallTo(() => _credentials.FindAsync("locked", A<CancellationToken>._)).Returns(locked);
        A.CallTo(() => _credentials.FindAsync("disabled", A<CancellationToken>._)).Returns(disabled);
        var sut = Authenticator();

        // Act
        var whileLocked = await sut.AuthenticateAsync(Basic("locked", "locked-password-12"), "peer", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(2));
        var afterLockout = await sut.AuthenticateAsync(Basic("locked", "locked-password-12"), "peer", TestContext.Current.CancellationToken);
        var whileDisabled = await sut.AuthenticateAsync(Basic("disabled", "disabled-password-12"), "peer", TestContext.Current.CancellationToken);

        // Assert
        whileLocked.ShouldBeNull();
        afterLockout.ShouldNotBeNull().Role.ShouldBe(CredentialRole.Ops);
        whileDisabled.ShouldBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_ForTheAdministrator_ShouldLockOutAfterTheThresholdAndDisableOnRequest()
    {
        // Arrange
        var sut = Authenticator();

        // Act: three wrong passwords, then the right one, then the right one after the lockout.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await sut.AuthenticateAsync(Basic("admin", "wrong"), "peer", TestContext.Current.CancellationToken)).ShouldBeNull();
        }

        var whileLocked = await sut.AuthenticateAsync(Basic("admin", "admin-password-12"), "peer", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(16));
        var afterLockout = await sut.AuthenticateAsync(Basic("admin", "admin-password-12"), "peer", TestContext.Current.CancellationToken);
        _settings.AdminDisabled = true;
        var whenDisabled = await Authenticator().AuthenticateAsync(Basic("admin", "admin-password-12"), "peer", TestContext.Current.CancellationToken);

        // Assert
        whileLocked.ShouldBeNull();
        afterLockout.ShouldNotBeNull();
        whenDisabled.ShouldBeNull("a disabled administrator is looked up in the table like any name, where it is not");
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTheHashWasMadeUnderOlderParameters_ShouldRemakeItOnSuccess()
    {
        // Arrange: a credential hashed with fewer iterations than the hasher now uses.
        var older = new PasswordHasher(new NightingaleOptionsBase.AuthSettings { AdminPassword = "x", Iterations = 10_000 });
        var row = new Credential { Id = Guid.NewGuid(), Name = "older", PasswordHash = older.Hash("older-password-12"), Role = CredentialRole.User, TenantId = Tenant };
        A.CallTo(() => _credentials.FindAsync("older", A<CancellationToken>._)).Returns(row);
        _settings.Iterations = 20_000;
        var sut = Authenticator();

        // Act
        var principal = await sut.AuthenticateAsync(Basic("older", "older-password-12"), "peer", TestContext.Current.CancellationToken);

        // Assert
        principal.ShouldNotBeNull();
        A.CallTo(() => _credentials.RecordSuccessAsync(row.Id, A<string>.That.Matches(hash => hash.StartsWith("$pbkdf2-sha256$i=20000,", StringComparison.Ordinal)), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    private static string Basic(string name, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(name + ":" + password));

    private BasicAuthenticator Authenticator() =>
        new(_settings, new PasswordHasher(_settings), _credentials, _time, NullLogger<BasicAuthenticator>.Instance);
}
