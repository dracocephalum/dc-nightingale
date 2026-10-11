using System.Text;

using Dracocephalum.Nightingale.Server.Auth;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// An append made through this process wakes the tail, so a live event reaches it well inside
/// the polling interval: with the interval set far longer than the test waits, the only way the
/// head reaches the append in time is the wake. Both tails, the store's own agent over a shared
/// sequence and the server's poller under a sequence per tenant, on a database of their own.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class TailWakingTests
{
    private static readonly TimeSpan FarLongerThanTheTest = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(PartitioningMode.None)]
    [InlineData(PartitioningMode.Tenant)]
    public async Task Head_WhenThisProcessAppends_ShouldReachTheAppendWithoutWaitingOutTheInterval(PartitioningMode partitioning)
    {
        // Arrange: a host whose tail would not look again for minutes on its own.
        var name = TestDatabases.NewName();
        try
        {
            using var host = await TestDatabases.StartProvisionedHostAsync(name, options =>
            {
                options.Store.Partitioning = partitioning;
                options.TailPollingInterval = FarLongerThanTheTest;
            });
            var stores = host.Services.GetRequiredService<ITenantStores>();
            var tail = stores.GetTail(TenantScope.Default);
            var store = stores.GetStreams(TenantScope.Default);

            // Act
            var appended = await store.AppendAsync("orders-" + Guid.NewGuid().ToString("N"), StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var head = tail.Head;
            while (head < appended.Position)
            {
                head = await tail.WaitForAdvanceAsync(head, timeout.Token);
            }

            // Assert
            head.ShouldBeGreaterThanOrEqualTo(appended.Position);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await TestDatabases.DropAsync(name);
        }
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{\"orderId\":1}"));
}
