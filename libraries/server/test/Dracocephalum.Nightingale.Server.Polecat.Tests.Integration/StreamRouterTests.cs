using System.Text;

using Dracocephalum.Nightingale.Server.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The routers over the real store, one per tenant: a stream name is per tenant, so a follower
/// of a name in one tenant gets that tenant's events and never another's, whether the tenants
/// share a sequence or each have their own. Each case uses a database of its own.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class StreamRouterTests
{
    [Theory]
    [InlineData(PartitioningMode.None)]
    [InlineData(PartitioningMode.Tenant)]
    public async Task Follow_ShouldHandAFollowerItsOwnTenantsEventsOfTheNameAndNotAnotherTenants(PartitioningMode partitioning)
    {
        var name = TestDatabases.NewName();
        try
        {
            // Arrange: the same stream name followed in two tenants.
            using var host = await TestDatabases.StartProvisionedHostAsync(name, options => options.Store.Partitioning = partitioning);
            var stores = host.Services.GetRequiredService<ITenantStores>();
            var tenant = await host.Services.GetRequiredService<ITenantStore>().CreateAsync("acme", TestContext.Current.CancellationToken);
            var acme = TenantScope.OfStoreTenant(tenant.StoreTenantId);
            await using var routers = new StreamRouters(stores);
            using var mine = routers.For(TenantScope.Default).Follow("orders-1");
            using var theirs = routers.For(acme).Follow("orders-1");

            // Act: the other tenant's event of the name, then one of my own tenant on another
            // stream, so that my router is known to have routed past both.
            var appended = await stores.GetStreams(acme).AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
            var marker = await stores.GetStreams(TenantScope.Default).AppendAsync("shipments-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var toTheirs = await theirs.Events.ReadAsync(timeout.Token);
            while (routers.For(TenantScope.Default).Routed < marker.Position)
            {
                await Task.Delay(50, timeout.Token);
            }

            // Assert
            toTheirs.Stream.ShouldBe("orders-1");
            toTheirs.Position.ShouldBe(appended.Position);
            mine.Events.TryRead(out _).ShouldBeFalse("the other tenant's orders-1 is not mine");
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await TestDatabases.DropAsync(name);
        }
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{\"orderId\":1}"));
}
