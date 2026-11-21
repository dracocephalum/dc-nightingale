using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using FakeItEasy;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>The management calls over scripted unary calls: what the client sends for a credential or a tenant, what it makes of the answer, and what a refusal becomes.</summary>
public sealed class NightingaleManagementTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task CreateCredentialAsync_ShouldSendTheNameThePasswordTheRoleAndTheTenant()
    {
        // Arrange
        var asked = new List<CreateCredentialRequest>();
        var answer = new Protocols.Grpc.V1.CredentialInfo
        {
            Name = "reader",
            Role = Protocols.Grpc.V1.CredentialRole.Ops,
            Tenant = Tenant.ToString("D"),
            CreatedAt = Timestamp.FromDateTimeOffset(At),
            PasswordChangedAt = Timestamp.FromDateTimeOffset(At),
        };
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new CredentialResponse { Credential = answer }));
        var sut = client.Management;

        // Act
        var created = await sut.CreateCredentialAsync("reader", "a-long-password", CredentialRole.Ops, Tenant, TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().ShouldBe(new CreateCredentialRequest { Name = "reader", Password = "a-long-password", Role = Protocols.Grpc.V1.CredentialRole.Ops, Tenant = Tenant.ToString("D") });
        created.ShouldBe(new CredentialInfo("reader", CredentialRole.Ops, Tenant, false, At, At, null));
    }

    [Fact]
    public async Task CreateCredentialAsync_WhenOnlyTheNameAndThePasswordAreGiven_ShouldAskForAGlobalUser()
    {
        // Arrange
        var asked = new List<CreateCredentialRequest>();
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new CredentialResponse { Credential = new Protocols.Grpc.V1.CredentialInfo { Name = "reader", Role = Protocols.Grpc.V1.CredentialRole.User } }));
        var sut = client.Management;

        // Act
        var created = await sut.CreateCredentialAsync("reader", "a-long-password", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var request = asked.ShouldHaveSingleItem();
        request.Role.ShouldBe(Protocols.Grpc.V1.CredentialRole.User);
        request.Tenant.ShouldBeEmpty();
        created.TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task CreateCredentialAsync_WhenTheNameIsEmpty_ShouldThrowBeforeCalling()
    {
        // Arrange: a strict invoker proves no call is made.
        await using var client = new NightingaleClient(A.Fake<CallInvoker>(options => options.Strict()));
        var sut = client.Management;

        // Act & Assert
        Should.Throw<ArgumentException>(() => sut.CreateCredentialAsync(string.Empty, "a-long-password", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCredentialAsync_WhenTheNameIsTaken_ShouldThrowThatTheCredentialExists()
    {
        // Arrange
        await using var client = new NightingaleClient(FakeUnaryCall.Failing<CreateCredentialRequest, CredentialResponse>(FakeUnaryCall.Failure(StatusCode.AlreadyExists, "CREDENTIAL_EXISTS")));
        var sut = client.Management;

        // Act & Assert
        await Should.ThrowAsync<CredentialExistsException>(() => sut.CreateCredentialAsync("reader", "a-long-password", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("00000000-0000-0000-0000-000000000000", "-")]
    [InlineData("11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111")]
    public async Task UpdateCredentialAsync_ShouldSendTheTenantAsKeptGlobalOrNamed(string? tenant, string sent)
    {
        // Arrange
        var asked = new List<UpdateCredentialRequest>();
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new CredentialResponse { Credential = new Protocols.Grpc.V1.CredentialInfo { Name = "reader" } }));
        var sut = client.Management;

        // Act
        await sut.UpdateCredentialAsync("reader", tenantId: tenant is null ? null : Guid.Parse(tenant), cancellationToken: TestContext.Current.CancellationToken);

        // Assert: what was left out is sent as nothing, so the server keeps it.
        var request = asked.ShouldHaveSingleItem();
        request.Name.ShouldBe("reader");
        request.Tenant.ShouldBe(sent);
        request.Role.ShouldBe(Protocols.Grpc.V1.CredentialRole.Unspecified);
        request.HasDisabled.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateCredentialAsync_WhenTheRoleAndTheFlagAreGiven_ShouldSendThemAndReturnTheCredentialAsItIs()
    {
        // Arrange
        var asked = new List<UpdateCredentialRequest>();
        var answer = new Protocols.Grpc.V1.CredentialInfo { Name = "reader", Role = Protocols.Grpc.V1.CredentialRole.Admin, Disabled = true, LockedUntil = Timestamp.FromDateTimeOffset(At) };
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new CredentialResponse { Credential = answer }));
        var sut = client.Management;

        // Act
        var updated = await sut.UpdateCredentialAsync("reader", CredentialRole.Admin, disabled: true, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var request = asked.ShouldHaveSingleItem();
        request.Role.ShouldBe(Protocols.Grpc.V1.CredentialRole.Admin);
        request.HasDisabled.ShouldBeTrue();
        request.Disabled.ShouldBeTrue();
        updated.Role.ShouldBe(CredentialRole.Admin);
        updated.IsDisabled.ShouldBeTrue();
        updated.LockedUntil.ShouldBe(At);
    }

    [Fact]
    public async Task SetCredentialPasswordAsync_ShouldSendTheNameAndThePassword()
    {
        // Arrange
        var asked = new List<SetPasswordRequest>();
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new CredentialResponse { Credential = new Protocols.Grpc.V1.CredentialInfo { Name = "reader" } }));
        var sut = client.Management;

        // Act
        var credential = await sut.SetCredentialPasswordAsync("reader", "another-long-password", TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().ShouldBe(new SetPasswordRequest { Name = "reader", Password = "another-long-password" });
        credential.Name.ShouldBe("reader");
    }

    [Fact]
    public async Task DeleteCredentialAsync_ShouldNameTheCredential()
    {
        // Arrange
        var asked = new List<DeleteCredentialRequest>();
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new DeleteCredentialResponse()));
        var sut = client.Management;

        // Act
        await sut.DeleteCredentialAsync("reader", TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().Name.ShouldBe("reader");
    }

    [Fact]
    public async Task DeleteCredentialAsync_WhenThereIsNoSuchCredential_ShouldThrowThatItIsNotFound()
    {
        // Arrange
        await using var client = new NightingaleClient(FakeUnaryCall.Failing<DeleteCredentialRequest, DeleteCredentialResponse>(FakeUnaryCall.Failure(StatusCode.NotFound, "CREDENTIAL_NOT_FOUND")));
        var sut = client.Management;

        // Act & Assert
        await Should.ThrowAsync<CredentialNotFoundException>(() => sut.DeleteCredentialAsync("reader", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListCredentialsAsync_ShouldReturnThemInTheServersOrder()
    {
        // Arrange
        var asked = new List<ListCredentialsRequest>();
        var answer = new ListCredentialsResponse
        {
            Credentials =
            {
                new Protocols.Grpc.V1.CredentialInfo { Name = "admin", Role = Protocols.Grpc.V1.CredentialRole.Admin },
                new Protocols.Grpc.V1.CredentialInfo { Name = "reader", Role = Protocols.Grpc.V1.CredentialRole.User, Tenant = Tenant.ToString("D") },
            },
        };
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, answer));
        var sut = client.Management;

        // Act
        var credentials = await sut.ListCredentialsAsync(TestContext.Current.CancellationToken);

        // Assert
        asked.Count.ShouldBe(1);
        credentials.Select(credential => (credential.Name, credential.Role, credential.TenantId)).ShouldBe([("admin", CredentialRole.Admin, null), ("reader", CredentialRole.User, Tenant)]);
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_ShouldSendTheCurrentPasswordAndTheNewOne()
    {
        // Arrange
        var asked = new List<ChangeOwnPasswordRequest>();
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new ChangeOwnPasswordResponse()));
        var sut = client.Management;

        // Act
        await sut.ChangeOwnPasswordAsync("a-long-password", "another-long-password", TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().ShouldBe(new ChangeOwnPasswordRequest { CurrentPassword = "a-long-password", NewPassword = "another-long-password" });
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_WhenTheCurrentPasswordIsNotRight_ShouldThrowThatAuthenticationFailed()
    {
        // Arrange
        await using var client = new NightingaleClient(FakeUnaryCall.Failing<ChangeOwnPasswordRequest, ChangeOwnPasswordResponse>(FakeUnaryCall.Failure(StatusCode.Unauthenticated, "AUTHENTICATION_FAILED")));
        var sut = client.Management;

        // Act & Assert
        await Should.ThrowAsync<AuthenticationFailedException>(() => sut.ChangeOwnPasswordAsync("a-long-password", "another-long-password", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateTenantAsync_ShouldSendTheNameAndReturnTheTenantWithItsId()
    {
        // Arrange
        var asked = new List<CreateTenantRequest>();
        var answer = new Protocols.Grpc.V1.TenantInfo { Id = Tenant.ToString("D"), Name = "billing", CreatedAt = Timestamp.FromDateTimeOffset(At) };
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new TenantResponse { Tenant = answer }));
        var sut = client.Management;

        // Act
        var created = await sut.CreateTenantAsync("billing", TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().Name.ShouldBe("billing");
        created.ShouldBe(new TenantInfo(Tenant, "billing", false, At));
    }

    [Fact]
    public async Task CreateTenantAsync_WhenTheNameIsTaken_ShouldThrowThatTheTenantExists()
    {
        // Arrange
        await using var client = new NightingaleClient(FakeUnaryCall.Failing<CreateTenantRequest, TenantResponse>(FakeUnaryCall.Failure(StatusCode.AlreadyExists, "TENANT_EXISTS")));
        var sut = client.Management;

        // Act & Assert
        await Should.ThrowAsync<TenantExistsException>(() => sut.CreateTenantAsync("billing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateTenantAsync_WhenNothingIsGiven_ShouldSendOnlyTheId()
    {
        // Arrange
        var asked = new List<UpdateTenantRequest>();
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new TenantResponse { Tenant = new Protocols.Grpc.V1.TenantInfo { Id = Tenant.ToString("D"), Name = "billing" } }));
        var sut = client.Management;

        // Act
        await sut.UpdateTenantAsync(Tenant, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: what was left out is sent as nothing, so the server keeps it.
        var request = asked.ShouldHaveSingleItem();
        request.Id.ShouldBe(Tenant.ToString("D"));
        request.Name.ShouldBeEmpty();
        request.HasDisabled.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateTenantAsync_WhenTheNameAndTheFlagAreGiven_ShouldSendThemAndReturnTheTenantAsItIs()
    {
        // Arrange
        var asked = new List<UpdateTenantRequest>();
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, new TenantResponse { Tenant = new Protocols.Grpc.V1.TenantInfo { Id = Tenant.ToString("D"), Name = "invoicing", Disabled = true } }));
        var sut = client.Management;

        // Act
        var updated = await sut.UpdateTenantAsync(Tenant, "invoicing", true, TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().ShouldBe(new UpdateTenantRequest { Id = Tenant.ToString("D"), Name = "invoicing", Disabled = true });
        (updated.Name, updated.IsDisabled).ShouldBe(("invoicing", true));
    }

    [Fact]
    public async Task ListTenantsAsync_ShouldReturnThemInTheServersOrder()
    {
        // Arrange
        var asked = new List<ListTenantsRequest>();
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var answer = new ListTenantsResponse
        {
            Tenants =
            {
                new Protocols.Grpc.V1.TenantInfo { Id = Tenant.ToString("D"), Name = "billing" },
                new Protocols.Grpc.V1.TenantInfo { Id = other.ToString("D"), Name = "shipping", Disabled = true },
            },
        };
        await using var client = new NightingaleClient(FakeUnaryCall.Answering(asked, answer));
        var sut = client.Management;

        // Act
        var tenants = await sut.ListTenantsAsync(TestContext.Current.CancellationToken);

        // Assert
        asked.Count.ShouldBe(1);
        tenants.Select(tenant => (tenant.Id, tenant.Name, tenant.IsDisabled)).ShouldBe([(Tenant, "billing", false), (other, "shipping", true)]);
    }
}
