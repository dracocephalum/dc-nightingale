using System.Text;

using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The ordinal sequencer's states against a real store: disabled by the operator, it numbers
/// nothing and the numbering of every sequence says so, and enabled again it goes on from where it
/// stopped; a sequence whose batches fail again and again is faulted, on its progress row too, and
/// numbers again, its fault cleared, once a batch succeeds. The failure is the links' table renamed
/// away for a while.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SequencerStateTests
{
    [Fact]
    public async Task Sequencer_WhenDisabled_ShouldNumberNothingAndSaySoAndGoOnFromWhereItStoppedOnceEnabled()
    {
        // Arrange
        var name = TestDatabases.NewName();
        try
        {
            long appended;
            using (var disabled = await TestDatabases.StartProvisionedHostAsync(name, options =>
            {
                options.Store.AssignOrdinals = true;
                options.Store.OrdinalSequencerEnabled = false;
            }))
            {
                // Act
                appended = (await disabled.Store().AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken)).Position;
                await disabled.Tail().RefreshAsync(TestContext.Current.CancellationToken);
                var stopped = disabled.Tail().NumberingStopped;
                var waited = await Should.ThrowAsync<OrdinalSequencerDisabledException>(() => disabled.Tail().WaitForNumberedAsync(appended, TestContext.Current.CancellationToken).AsTask());
                var links = await TestDatabases.ScalarAsync<int>(name, "SELECT COUNT(*) FROM nightingale.OrdinalLink");
                await disabled.StopAsync(TestContext.Current.CancellationToken);

                // Assert
                stopped.ShouldBeOfType<OrdinalSequencerDisabledException>();
                waited.ShouldNotBeNull();
                links.ShouldBe(0);
            }

            using var enabled = await TestDatabases.StartHostAsync(name, TestDatabases.FastBoot(options => options.Store.AssignOrdinals = true));
            var numbered = await NumberedAsync(enabled.Tail(), appended);
            await enabled.StopAsync(TestContext.Current.CancellationToken);
            numbered.ShouldBeGreaterThanOrEqualTo(appended);
            enabled.Tail().NumberingStopped.ShouldBeNull();
        }
        finally
        {
            await TestDatabases.DropAsync(name);
        }
    }

    [Fact]
    public async Task Sequencer_WhenASequenceFailsAgainAndAgain_ShouldFaultItOnItsRowAndClearTheFaultOnceABatchSucceeds()
    {
        // Arrange: a short lease, so the passes after a failure come a third of a second apart.
        var name = TestDatabases.NewName();
        try
        {
            using var host = await TestDatabases.StartProvisionedHostAsync(name, options =>
            {
                options.Store.AssignOrdinals = true;
                options.OrdinalSequencerLeaseDuration = TimeSpan.FromMilliseconds(900);
            });
            var tail = host.Tail();
            var first = await host.Store().AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
            await NumberedAsync(tail, first.Position);

            // Act: the links' table goes away, so every batch fails; then it comes back.
            await TestDatabases.ExecuteAsync(name, "EXEC sp_rename 'nightingale.OrdinalLink', 'OrdinalLinkAway';");
            var second = await host.Store().AppendAsync("orders-2", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
            await UntilAsync(() => tail.NumberingStopped is not null);
            var stopped = tail.NumberingStopped;
            await UntilAsync(async () => await TestDatabases.ScalarAsync<int>(name, "SELECT COUNT(*) FROM nightingale.OrdinalSequencingProgress WHERE [TenantId] IS NULL AND [Fault] IS NOT NULL") == 1);
            var recorded = await TestDatabases.ScalarAsync<string>(name, "SELECT [Fault] FROM nightingale.OrdinalSequencingProgress WHERE [TenantId] IS NULL");
            await TestDatabases.ExecuteAsync(name, "EXEC sp_rename 'nightingale.OrdinalLinkAway', 'OrdinalLink';");
            var numbered = await NumberedAsync(tail, second.Position);
            await UntilAsync(async () => await TestDatabases.ScalarAsync<int>(name, "SELECT COUNT(*) FROM nightingale.OrdinalSequencingProgress WHERE [Fault] IS NOT NULL") == 0);
            await host.StopAsync(TestContext.Current.CancellationToken);

            // Assert
            stopped.ShouldBeOfType<OrdinalSequencerFaultedException>();
            recorded.ShouldNotBeNullOrWhiteSpace();
            numbered.ShouldBeGreaterThanOrEqualTo(second.Position);
            tail.NumberingStopped.ShouldBeNull();
        }
        finally
        {
            await TestDatabases.DropAsync(name);
        }
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    private static async Task<long> NumberedAsync(IStoreTail tail, long position)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                return await tail.WaitForNumberedAsync(position, timeout.Token);
            }
            catch (OrdinalSequencerFaultedException)
            {
                // Still faulted from before the table came back: the next batch clears it.
                await Task.Delay(100, timeout.Token);
            }
        }
    }

    private static Task UntilAsync(Func<bool> condition) => UntilAsync(() => Task.FromResult(condition()));

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        (await condition()).ShouldBeTrue("the condition did not hold within twenty seconds");
    }
}
