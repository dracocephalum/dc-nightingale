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
/// The services behind the interceptor, through a test host with authentication on: who gets
/// in, what each role may do, and how the tenant header is read for bound and global
/// credentials. The stores are fakes; the credentials and tenants are rows in an in-memory
/// gateway database.
/// </summary>
public sealed class AuthorizationTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-password-12";
    private static readonly Guid Billing = Guid.NewGuid();
    private static readonly Guid Shipping = Guid.NewGuid();

    private readonly IStreamStore _store = A.Fake<IStreamStore>();
    private readonly ISubscriptionGroupStore _groups = A.Fake<ISubscriptionGroupStore>();
    private readonly ITenantStores _stores = A.Fake<ITenantStores>();
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
        builder.Services.AddSingleton(_stores);
        builder.Services.AddSingleton<IStoreTail>(_tail);
        A.CallTo(() => _stores.GetStreams(A<TenantScope>._)).Returns(_store);
        A.CallTo(() => _stores.GetGroups(A<TenantScope>.That.Matches(scope => !scope.IsWildcard))).Returns(_groups);
        A.CallTo(() => _stores.GetGroups(A<TenantScope>.That.Matches(scope => scope.IsWildcard))).Throws(new ArgumentException("Persistent subscriptions are per tenant; the wildcard has none."));
        _app = builder.Build();
        _app.MapNightingaleServer();

        // The default tenant as the schema writes it, a second tenant, and three credentials.
        var contexts = _app.Services.GetRequiredService<IDbContextFactory<NightingaleDbContext>>();
        var hasher = _app.Services.GetRequiredService<PasswordHasher>();
        await using (var context = await contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.EnsureDefaultTenantAsync(DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);
            context.Tenants.Add(new Tenant { Id = Billing, Name = "billing", StoreTenantId = "2" });
            context.Tenants.Add(new Tenant { Id = Shipping, Name = "shipping", StoreTenantId = "3", IsDisabled = true });
            context.Credentials.Add(new Credential { Id = Guid.NewGuid(), Name = "reader", PasswordHash = hasher.Hash("reader-password-12"), Role = CredentialRole.User, TenantId = Tenant.DefaultId });
            context.Credentials.Add(new Credential { Id = Guid.NewGuid(), Name = "operator", PasswordHash = hasher.Hash("operator-password-12"), Role = CredentialRole.Ops, TenantId = Tenant.DefaultId });
            context.Credentials.Add(new Credential { Id = Guid.NewGuid(), Name = "auditor", PasswordHash = hasher.Hash("auditor-password-12"), Role = CredentialRole.User, TenantId = null });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        A.CallTo(() => _groups.GetAsync(A<string>._, A<string>._, A<CancellationToken>._)).Returns((SubscriptionGroupDefinition?)null);
        A.CallTo(() => _groups.DeleteAsync(A<string>._, A<string>._, A<CancellationToken>._)).Returns(true);
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
    public async Task Ping_ShouldNeedNoCredentialsAndSayThatTheRestDoes()
    {
        // Arrange
        var client = new ServerFeatures.ServerFeaturesClient(_channel);

        // Act
        var response = await client.PingAsync(new PingRequest(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        response.AuthenticationRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task Calls_WithoutCredentialsOrWithWrongOnes_ShouldBeRefusedAsUnauthenticated()
    {
        // Arrange
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var none = await Refusal(async () => await client.ListAsync(new ListRequest(), cancellationToken: TestContext.Current.CancellationToken));
        var wrong = await Refusal(async () => await client.ListAsync(new ListRequest(), Headers("admin", "nope"), cancellationToken: TestContext.Current.CancellationToken));
        var bearer = await Refusal(async () => await client.ListAsync(new ListRequest(), new Metadata { { "authorization", "Bearer token" } }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        foreach (var refusal in new[] { none, wrong, bearer })
        {
            refusal.StatusCode.ShouldBe(StatusCode.Unauthenticated);
            refusal.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull().Reason.ShouldBe("AUTHENTICATION_FAILED");
        }
    }

    [Fact]
    public async Task Calls_ShouldBeGatedByRole()
    {
        // Arrange
        A.CallTo(() => _groups.ListAsync(null, A<CancellationToken>._)).Returns(new List<SubscriptionGroupSummary>());
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var asReader = await Refusal(async () => await client.ListAsync(new ListRequest(), Headers("reader", "reader-password-12"), cancellationToken: TestContext.Current.CancellationToken));
        var asOperator = await client.ListAsync(new ListRequest(), Headers("operator", "operator-password-12"), cancellationToken: TestContext.Current.CancellationToken);
        var asAdmin = await client.ListAsync(new ListRequest(), Headers("admin", AdminPassword, Tenant.DefaultId), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        asReader.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        asReader.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull().Metadata["required"].ShouldBe("ops");
        asOperator.Groups.ShouldBeEmpty();
        asAdmin.Groups.ShouldBeEmpty();
    }

    [Fact]
    public async Task TenantHeader_ForABoundCredential_ShouldBeOptionalAndRefusedWhenItNamesAnother()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, A<long?>._, A<int>._, A<CancellationToken>._)).Returns(new StreamSlice(new StreamHead(0, -1), []));
        var client = new Streams.StreamsClient(_channel);
        var request = new ReadRequest { Stream = "orders-1", Start = new(), Count = 1 };

        // Act
        var own = await ReadAll(client, request, Headers("reader", "reader-password-12"));
        var ownNamed = await ReadAll(client, request, Headers("reader", "reader-password-12", Tenant.DefaultId));
        var other = await Refusal(async () => await ReadAll(client, request, Headers("reader", "reader-password-12", Billing)));

        // Assert
        own.ShouldNotBeNull();
        ownNamed.ShouldNotBeNull();
        other.StatusCode.ShouldBe(StatusCode.NotFound);
        other.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull().Reason.ShouldBe("TENANT_NOT_FOUND");
    }

    [Fact]
    public async Task TenantHeader_ForAGlobalCredential_ShouldBeRequiredResolvedAndWildcardOnReadsOnly()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, A<long?>._, A<int>._, A<CancellationToken>._)).Returns(new StreamSlice(new StreamHead(0, -1), []));
        var streams = new Streams.StreamsClient(_channel);
        var groups = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        var read = new ReadRequest { Stream = "orders-1", Start = new(), Count = 1 };
        var readAll = new ReadRequest { Stream = "$all", Start = new(), Count = 1 };

        // Act
        var none = await Refusal(async () => await ReadAll(streams, read, Headers("auditor", "auditor-password-12")));
        var named = await ReadAll(streams, read, Headers("auditor", "auditor-password-12", Tenant.DefaultId));
        var wildcard = await ReadAll(streams, readAll, Headers("auditor", "auditor-password-12", "*"));
        var unknown = await Refusal(async () => await ReadAll(streams, read, Headers("auditor", "auditor-password-12", Guid.NewGuid())));
        var disabled = await Refusal(async () => await ReadAll(streams, read, Headers("auditor", "auditor-password-12", Shipping)));
        var otherTenant = await ReadAll(streams, read, Headers("admin", AdminPassword, Billing));
        var wildcardWrite = await Refusal(async () => await groups.DeleteAsync(new DeleteGroupRequest { Stream = "orders-1", Group = "g" }, Headers("admin", AdminPassword, "*"), cancellationToken: TestContext.Current.CancellationToken));

        // Assert: each call resolves its store for the tenant it was authorized for.
        none.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull().Reason.ShouldBe("TENANT_NOT_FOUND");
        named.ShouldNotBeNull();
        wildcard.ShouldNotBeNull();
        unknown.StatusCode.ShouldBe(StatusCode.NotFound);
        disabled.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        disabled.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull().Reason.ShouldBe("TENANT_DISABLED");
        otherTenant.ShouldNotBeNull();
        wildcardWrite.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        A.CallTo(() => _stores.GetStreams(A<TenantScope>.That.Matches(scope => scope.TenantId == Tenant.DefaultId && scope.StoreTenantId == Tenant.DefaultStoreTenantId))).MustHaveHappened();
        A.CallTo(() => _stores.GetStreams(A<TenantScope>.That.Matches(scope => scope.TenantId == Billing && scope.StoreTenantId == "2"))).MustHaveHappened();
        A.CallTo(() => _stores.GetStreams(A<TenantScope>.That.Matches(scope => scope.IsWildcard))).MustHaveHappened();
        A.CallTo(() => _stores.GetStreams(A<TenantScope>.That.Matches(scope => scope.TenantId == Shipping))).MustNotHaveHappened();
    }

    private static Metadata Headers(string name, string password, object? tenant = null)
    {
        var headers = new Metadata { { "authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(name + ":" + password)) } };
        if (tenant is not null)
        {
            headers.Add(Authorization.TenantHeader, tenant is Guid id ? id.ToString("D") : tenant.ToString()!);
        }

        return headers;
    }

    private static async Task<RpcException> Refusal(Func<Task> call) =>
        await Should.ThrowAsync<RpcException>(call);

    private static async Task<List<ReadResponse>> ReadAll(Streams.StreamsClient client, ReadRequest request, Metadata headers)
    {
        using var call = client.Read(request, headers, cancellationToken: TestContext.Current.CancellationToken);
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
