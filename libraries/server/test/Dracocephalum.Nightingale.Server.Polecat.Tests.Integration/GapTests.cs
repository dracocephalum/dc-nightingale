using System.Text;

using Dracocephalum.Nightingale.Server.Auth;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// Numbers the sequence handed out and nobody committed, as a rolled-back append or a restart
/// of the server leaves them, are a gap no transaction will ever fill: the tail gives it up after
/// the store's stale threshold and the head passes it, or every read and subscription by position
/// waits on it for good. The identity is moved on by hand here, which loses numbers exactly as a
/// restart does, with no transaction to hold them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GapTests
{
    [Fact]
    public async Task Head_WhenNumbersAreLostBelowAnAppend_ShouldGiveThemUpAndPassThem()
    {
        // Arrange
        var name = TestDatabases.NewName();
        try
        {
            using var host = await TestDatabases.StartProvisionedHostAsync(name);
            var stores = host.Services.GetRequiredService<ITenantStores>();
            var tail = stores.GetTail(TenantScope.Default);
            var store = stores.GetStreams(TenantScope.Default);
            var before = await store.AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
            await TestDatabases.ExecuteAsync(name, "DECLARE @reseed nvarchar(200) = N'DBCC CHECKIDENT (''dbo.pc_events'', RESEED, ' + CAST(CAST(IDENT_CURRENT('dbo.pc_events') AS bigint) + 5 AS nvarchar(20)) + N')'; EXEC (@reseed);");

            // Act
            var after = await store.AppendAsync("orders-2", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var head = tail.Head;
            while (head < after.Position && !timeout.IsCancellationRequested)
            {
                try
                {
                    head = await tail.WaitForAdvanceAsync(head, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                }
            }

            // Assert: the numbers were lost, and the head went past them.
            after.Position.ShouldBeGreaterThan(before.Position + 1);
            head.ShouldBeGreaterThanOrEqualTo(after.Position, $"the head stayed at {head}, held by the numbers lost after {before.Position}, for 30 s against a stale threshold of 3 s");
        }
        finally
        {
            await TestDatabases.DropAsync(name);
        }
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{\"orderId\":1}"));
}
