using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Auth;

/// <summary>The directory over an in-memory table: what it keeps, what it reads again, and what a disable does.</summary>
public sealed class TenantDirectoryTests
{
    private static readonly Guid Billing = Guid.NewGuid();
    private readonly DbContextOptions<NightingaleDbContext> _options = new DbContextOptionsBuilder<NightingaleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
    private readonly SubscriptionGroupRegistry _registry = new();

    [Fact]
    public async Task FindAsync_ShouldReadATenantOnceAndKeepItsStoreId()
    {
        // Arrange
        await using (var context = new NightingaleDbContext(_options, new NightingaleSchema("nightingale")))
        {
            context.Tenants.Add(new Tenant { Id = Billing, Name = "billing", StoreTenantId = "7" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var sut = Directory();

        // Act
        var first = await sut.FindAsync(Billing, TestContext.Current.CancellationToken);
        await using (var context = new NightingaleDbContext(_options, new NightingaleSchema("nightingale")))
        {
            context.Tenants.RemoveRange(context.Tenants);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var second = await sut.FindAsync(Billing, TestContext.Current.CancellationToken);
        var unknown = await sut.FindAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBe(new TenantDirectory.TenantEntry(Billing, "7", false));
        second.ShouldBe(first, "the mapping is immutable, so it is kept without asking again");
        unknown.ShouldBeNull();
    }

    [Fact]
    public async Task RefreshAsync_WhenATenantWasDisabledElsewhere_ShouldFlipTheFlagAndStopItsGroupsOnce()
    {
        // Arrange: a tenant known as enabled, then disabled in the table by another instance.
        await using (var context = new NightingaleDbContext(_options, new NightingaleSchema("nightingale")))
        {
            context.Tenants.Add(new Tenant { Id = Billing, Name = "billing", StoreTenantId = "7" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var sut = Directory();
        await sut.FindAsync(Billing, TestContext.Current.CancellationToken);
        await using (var context = new NightingaleDbContext(_options, new NightingaleSchema("nightingale")))
        {
            (await context.Tenants.SingleAsync(TestContext.Current.CancellationToken)).IsDisabled = true;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await sut.RefreshAsync(TestContext.Current.CancellationToken);
        var disabled = await sut.FindAsync(Billing, TestContext.Current.CancellationToken);
        await using (var context = new NightingaleDbContext(_options, new NightingaleSchema("nightingale")))
        {
            (await context.Tenants.SingleAsync(TestContext.Current.CancellationToken)).IsDisabled = false;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await sut.RefreshAsync(TestContext.Current.CancellationToken);
        var enabled = await sut.FindAsync(Billing, TestContext.Current.CancellationToken);

        // Assert
        disabled.ShouldNotBeNull().IsDisabled.ShouldBeTrue();
        enabled.ShouldNotBeNull().IsDisabled.ShouldBeFalse();
    }

    private TenantDirectory Directory() =>
        new(new Contexts(_options), _registry, NullLogger<TenantDirectory>.Instance);

    private sealed class Contexts(DbContextOptions<NightingaleDbContext> options) : IDbContextFactory<NightingaleDbContext>
    {
        public NightingaleDbContext CreateDbContext() => new(options, new NightingaleSchema("nightingale"));
    }
}
