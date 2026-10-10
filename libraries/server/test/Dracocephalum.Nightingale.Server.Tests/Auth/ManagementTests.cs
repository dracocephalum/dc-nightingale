using System.Text;

using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Grpc;
using FakeItEasy;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Auth;

/// <summary>
/// The credentials and tenants services through a test host with authentication on: what an
/// admin makes, what a tenant-bound admin is kept to, what a credential made here can then do,
/// and what disabling a tenant does to its caller.
/// </summary>
public sealed class ManagementTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-password-12";

    private readonly IStreamStore _store = A.Fake<IStreamStore>();
    private readonly ISubscriptionGroupStore _groups = A.Fake<ISubscriptionGroupStore>();
    private readonly FakeTail _tail = new();
    private readonly string _database = Guid.NewGuid().ToString("N");
    private WebApplication? _app;
    private GrpcChannel? _channel;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNightingaleServer();
        builder.Services.AddNightingaleOptions(new TestOptions { Auth = { AdminPassword = AdminPassword, AllowInsecureTransport = true, Iterations = 10_000 } });
        builder.Services.AddNightingaleSubscriptionGroupStore("nightingale", Tenant.DefaultStoreTenantId, options => options.UseInMemoryDatabase(_database));
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(_groups);
        builder.Services.AddSingleton<IStoreTail>(_tail);
        _app = builder.Build();
        _app.MapNightingaleServer();
        var contexts = _app.Services.GetRequiredService<IDbContextFactory<NightingaleDbContext>>();
        await using (var context = await contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.EnsureDefaultTenantAsync(DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);
        }

        A.CallTo(() => _groups.ListAsync(null, A<CancellationToken>._)).Returns(new List<SubscriptionGroupSummary>());
        await _app.StartAsync();
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _app.GetTestServer().CreateHandler() });
    }

    public async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Credentials_ShouldBeMadeUsedChangedAndDeletedByAnAdminWithinTheRules()
    {
        // Arrange
        var credentials = new Protocols.Grpc.V1.Credentials.CredentialsClient(_channel);
        var groups = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        var admin = Headers("admin", AdminPassword);

        // Act: an ops credential and a user, both bound to the default tenant; the user tries
        // management, the ops credential succeeds; the user changes its own password; the
        // admin disables the ops credential, which is refused at once on this instance.
        var made = await credentials.CreateAsync(new CreateCredentialRequest { Name = "operator", Password = "operator-password-12", Role = Protocols.Grpc.V1.CredentialRole.Ops, Tenant = Tenant.DefaultId.ToString("D") }, admin, cancellationToken: TestContext.Current.CancellationToken);
        await credentials.CreateAsync(new CreateCredentialRequest { Name = "reader", Password = "reader-password-12", Tenant = Tenant.DefaultId.ToString("D") }, admin, cancellationToken: TestContext.Current.CancellationToken);
        var duplicate = await Refusal(async () => await credentials.CreateAsync(new CreateCredentialRequest { Name = "reader", Password = "reader-password-12", Tenant = Tenant.DefaultId.ToString("D") }, admin, cancellationToken: TestContext.Current.CancellationToken));
        var tooShort = await Refusal(async () => await credentials.CreateAsync(new CreateCredentialRequest { Name = "short", Password = "short", Tenant = Tenant.DefaultId.ToString("D") }, admin, cancellationToken: TestContext.Current.CancellationToken));
        var readerManages = await Refusal(async () => await credentials.ListAsync(new ListCredentialsRequest(), Headers("reader", "reader-password-12"), cancellationToken: TestContext.Current.CancellationToken));
        var operatorLists = await groups.ListAsync(new ListRequest(), Headers("operator", "operator-password-12"), cancellationToken: TestContext.Current.CancellationToken);
        await credentials.ChangeOwnPasswordAsync(new ChangeOwnPasswordRequest { CurrentPassword = "reader-password-12", NewPassword = "reader-password-13" }, Headers("reader", "reader-password-12"), cancellationToken: TestContext.Current.CancellationToken);
        var oldPassword = await Refusal(async () => await credentials.ChangeOwnPasswordAsync(new ChangeOwnPasswordRequest { CurrentPassword = "reader-password-12", NewPassword = "reader-password-14" }, Headers("reader", "reader-password-12"), cancellationToken: TestContext.Current.CancellationToken));
        var disabled = await credentials.UpdateAsync(new UpdateCredentialRequest { Name = "operator", Disabled = true }, admin, cancellationToken: TestContext.Current.CancellationToken);
        var operatorRefused = await Refusal(async () => await groups.ListAsync(new ListRequest(), Headers("operator", "operator-password-12"), cancellationToken: TestContext.Current.CancellationToken));
        var listed = await credentials.ListAsync(new ListCredentialsRequest(), Headers("admin", AdminPassword, Tenant.DefaultId), cancellationToken: TestContext.Current.CancellationToken);
        await credentials.DeleteAsync(new DeleteCredentialRequest { Name = "operator" }, admin, cancellationToken: TestContext.Current.CancellationToken);
        var gone = await Refusal(async () => await credentials.DeleteAsync(new DeleteCredentialRequest { Name = "operator" }, admin, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        made.Credential.Role.ShouldBe(Protocols.Grpc.V1.CredentialRole.Ops);
        made.Credential.Tenant.ShouldBe(Tenant.DefaultId.ToString("D"));
        Reason(duplicate).ShouldBe("CREDENTIAL_EXISTS");
        tooShort.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        Reason(readerManages).ShouldBe("ACCESS_DENIED");
        operatorLists.Groups.ShouldBeEmpty();
        Reason(oldPassword).ShouldBe("AUTHENTICATION_FAILED");
        disabled.Credential.Disabled.ShouldBeTrue();
        Reason(operatorRefused).ShouldBe("AUTHENTICATION_FAILED");
        listed.Credentials.Select(credential => credential.Name).ShouldBe(["operator", "reader"]);
        Reason(gone).ShouldBe("CREDENTIAL_NOT_FOUND");
    }

    [Fact]
    public async Task Credentials_ByATenantBoundAdmin_ShouldStayWithinItsTenantAndRole()
    {
        // Arrange: a tenant-bound admin of a second tenant, made by the global admin.
        var credentials = new Protocols.Grpc.V1.Credentials.CredentialsClient(_channel);
        var tenants = new Protocols.Grpc.V1.Tenants.TenantsClient(_channel);
        var admin = Headers("admin", AdminPassword);
        var billing = (await tenants.CreateAsync(new CreateTenantRequest { Name = "billing" }, admin, cancellationToken: TestContext.Current.CancellationToken)).Tenant;
        await credentials.CreateAsync(new CreateCredentialRequest { Name = "billing-admin", Password = "billing-password-12", Role = Protocols.Grpc.V1.CredentialRole.Admin, Tenant = billing.Id }, admin, cancellationToken: TestContext.Current.CancellationToken);
        await credentials.CreateAsync(new CreateCredentialRequest { Name = "elsewhere", Password = "elsewhere-password-12", Tenant = Tenant.DefaultId.ToString("D") }, admin, cancellationToken: TestContext.Current.CancellationToken);
        var bound = Headers("billing-admin", "billing-password-12");

        // Act
        var ownTenant = await credentials.CreateAsync(new CreateCredentialRequest { Name = "billing-ops", Password = "billing-ops-password-12", Role = Protocols.Grpc.V1.CredentialRole.Ops, Tenant = billing.Id }, bound, cancellationToken: TestContext.Current.CancellationToken);
        var otherTenant = await Refusal(async () => await credentials.CreateAsync(new CreateCredentialRequest { Name = "intruder", Password = "intruder-password-12", Tenant = Tenant.DefaultId.ToString("D") }, bound, cancellationToken: TestContext.Current.CancellationToken));
        var global = await Refusal(async () => await credentials.CreateAsync(new CreateCredentialRequest { Name = "intruder", Password = "intruder-password-12" }, bound, cancellationToken: TestContext.Current.CancellationToken));
        var othersRow = await Refusal(async () => await credentials.SetPasswordAsync(new SetPasswordRequest { Name = "elsewhere", Password = "changed-password-12" }, bound, cancellationToken: TestContext.Current.CancellationToken));
        var listed = await credentials.ListAsync(new ListCredentialsRequest(), bound, cancellationToken: TestContext.Current.CancellationToken);
        var tenantsByBound = await Refusal(async () => await tenants.ListAsync(new ListTenantsRequest(), bound, cancellationToken: TestContext.Current.CancellationToken));
        var aboveRole = await Refusal(async () => await credentials.UpdateAsync(new UpdateCredentialRequest { Name = "billing-ops", Role = Protocols.Grpc.V1.CredentialRole.Admin }, Headers("billing-ops", "billing-ops-password-12"), cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        ownTenant.Credential.Tenant.ShouldBe(billing.Id);
        Reason(otherTenant).ShouldBe("ACCESS_DENIED");
        Reason(global).ShouldBe("ACCESS_DENIED");
        Reason(othersRow).ShouldBe("CREDENTIAL_NOT_FOUND", "another tenant's credential is not revealed");
        listed.Credentials.Select(credential => credential.Name).ShouldBe(["billing-admin", "billing-ops"]);
        Reason(tenantsByBound).ShouldBe("ACCESS_DENIED");
        Reason(aboveRole).ShouldBe("ACCESS_DENIED");
    }

    [Fact]
    public async Task Tenants_ShouldBeMadeRenamedAndDisabledByAGlobalAdminAndRefuseTheirCallersWhenDisabled()
    {
        // Arrange
        var tenants = new Protocols.Grpc.V1.Tenants.TenantsClient(_channel);
        var credentials = new Protocols.Grpc.V1.Credentials.CredentialsClient(_channel);
        var streams = new Streams.StreamsClient(_channel);
        var admin = Headers("admin", AdminPassword);
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, A<long?>._, A<int>._, A<CancellationToken>._)).Returns(new StreamSlice(new StreamHead(0, -1), []));

        // Act
        var made = (await tenants.CreateAsync(new CreateTenantRequest { Name = "shipping" }, admin, cancellationToken: TestContext.Current.CancellationToken)).Tenant;
        var duplicate = await Refusal(async () => await tenants.CreateAsync(new CreateTenantRequest { Name = "shipping" }, admin, cancellationToken: TestContext.Current.CancellationToken));
        var renamed = (await tenants.UpdateAsync(new UpdateTenantRequest { Id = made.Id, Name = "shipping-eu" }, admin, cancellationToken: TestContext.Current.CancellationToken)).Tenant;
        var listed = (await tenants.ListAsync(new ListTenantsRequest(), admin, cancellationToken: TestContext.Current.CancellationToken)).Tenants;

        // A credential bound to the default tenant reads; the tenant is disabled; the read is refused.
        await credentials.CreateAsync(new CreateCredentialRequest { Name = "reader", Password = "reader-password-12", Tenant = Tenant.DefaultId.ToString("D") }, admin, cancellationToken: TestContext.Current.CancellationToken);
        var before = await ReadAll(streams, Headers("reader", "reader-password-12"));
        await tenants.UpdateAsync(new UpdateTenantRequest { Id = Tenant.DefaultId.ToString("D"), Disabled = true }, admin, cancellationToken: TestContext.Current.CancellationToken);
        var whileDisabled = await Refusal(async () => await ReadAll(streams, Headers("reader", "reader-password-12")));
        await tenants.UpdateAsync(new UpdateTenantRequest { Id = Tenant.DefaultId.ToString("D"), Disabled = false }, admin, cancellationToken: TestContext.Current.CancellationToken);
        var after = await ReadAll(streams, Headers("reader", "reader-password-12"));

        // Assert
        Guid.TryParseExact(made.Id, "D", out _).ShouldBeTrue();
        Reason(duplicate).ShouldBe("TENANT_EXISTS");
        renamed.Name.ShouldBe("shipping-eu");
        listed.Select(tenant => tenant.Name).ShouldBe(["default", "shipping-eu"]);
        before.ShouldNotBeNull();
        Reason(whileDisabled).ShouldBe("TENANT_DISABLED");
        after.ShouldNotBeNull();
    }

    private static Metadata Headers(string name, string password, Guid? tenant = null)
    {
        var headers = new Metadata { { "authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(name + ":" + password)) } };
        if (tenant is { } id)
        {
            headers.Add(Authorization.TenantHeader, id.ToString("D"));
        }

        return headers;
    }

    private static string Reason(RpcException exception) =>
        exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull().Reason;

    private static async Task<RpcException> Refusal(Func<Task> call) =>
        await Should.ThrowAsync<RpcException>(call);

    private static async Task<List<ReadResponse>> ReadAll(Streams.StreamsClient client, Metadata headers)
    {
        using var call = client.Read(new ReadRequest { Stream = "orders-1", Start = new(), Count = 1 }, headers, cancellationToken: TestContext.Current.CancellationToken);
        var responses = new List<ReadResponse>();
        await foreach (var response in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            responses.Add(response);
        }

        return responses;
    }

    private sealed class TestOptions : NightingaleOptionsBase
    {
    }
}
