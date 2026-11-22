using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Auth;

/// <summary>The loop that reads the tenants again, on a clock moved by hand: what a tick brings, and that a tick that fails is not the last.</summary>
public sealed class TenantRefresherTests
{
    private static readonly Guid Billing = Guid.NewGuid();
    private readonly FailingContexts _contexts = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
    private readonly TestOptions _options = new();

    [Fact]
    public async Task Run_ShouldNoticeAtATickATenantDisabledElsewhereAndNotBefore()
    {
        // Arrange: a tenant known as enabled, then disabled in the table by another instance.
        await _contexts.Inner.SeedAsync(new Tenant { Id = Billing, Name = "billing", StoreTenantId = "7" });
        var directory = Directory();
        await directory.FindAsync(Billing, TestContext.Current.CancellationToken);
        await DisableAsync();
        using var sut = new TenantRefresher(directory, _options, _time, NullLogger<TenantRefresher>.Instance);

        // Act
        await sut.StartAsync(TestContext.Current.CancellationToken);
        var beforeTheTick = await directory.FindAsync(Billing, TestContext.Current.CancellationToken);
        await TickUntilAsync(directory, disabled: true);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        beforeTheTick.ShouldNotBeNull().IsDisabled.ShouldBeFalse();
        sut.ExecuteTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue("stopping the host is not a failure of the loop");
    }

    [Fact]
    public async Task Run_WhenATickCannotReadTheTenants_ShouldReadThemAtTheNext()
    {
        // Arrange: the table cannot be read at first; the tenant is disabled in it meanwhile.
        await _contexts.Inner.SeedAsync(new Tenant { Id = Billing, Name = "billing", StoreTenantId = "7" });
        var directory = Directory();
        await directory.FindAsync(Billing, TestContext.Current.CancellationToken);
        await DisableAsync();
        _contexts.Failing = true;
        using var sut = new TenantRefresher(directory, _options, _time, NullLogger<TenantRefresher>.Instance);
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await TickUntilAsync(() => Task.FromResult(_contexts.Failures >= 2));
        var whileFailing = await directory.FindAsync(Billing, TestContext.Current.CancellationToken);

        // Act
        _contexts.Failing = false;
        await TickUntilAsync(directory, disabled: true);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert: what was known stays known while the table cannot be read.
        whileFailing.ShouldNotBeNull().IsDisabled.ShouldBeFalse();
    }

    private TenantDirectory Directory() => new(_contexts, new SubscriptionGroupRegistry(), NullLogger<TenantDirectory>.Instance);

    private async Task DisableAsync()
    {
        await using var context = _contexts.Inner.CreateDbContext();
        (await context.Tenants.SingleAsync(TestContext.Current.CancellationToken)).IsDisabled = true;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private Task TickUntilAsync(TenantDirectory directory, bool disabled) =>
        TickUntilAsync(async () => (await directory.FindAsync(Billing, TestContext.Current.CancellationToken))?.IsDisabled == disabled);

    /// <summary>Moves the clock a refresh interval at a time until a condition holds, or fails after a hundred ticks.</summary>
    private async Task TickUntilAsync(Func<Task<bool>> condition)
    {
        for (var tick = 0; tick < 100 && !await condition(); tick++)
        {
            _time.Advance(_options.Tenants.RefreshInterval);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        (await condition()).ShouldBeTrue("the loop did not get there in a hundred ticks");
    }

    /// <summary>The in-memory contexts, which a test can make unreachable as a database is when it is down.</summary>
    private sealed class FailingContexts : IDbContextFactory<NightingaleDbContext>
    {
        private int _failures;

        public InMemoryContexts Inner { get; } = new();

        public bool Failing { get; set; }

        public int Failures => Volatile.Read(ref _failures);

        public NightingaleDbContext CreateDbContext()
        {
            if (Failing)
            {
                Interlocked.Increment(ref _failures);
                throw new InvalidOperationException("The database is down.");
            }

            return Inner.CreateDbContext();
        }
    }

    private sealed class TestOptions : NightingaleOptionsBase
    {
    }
}
