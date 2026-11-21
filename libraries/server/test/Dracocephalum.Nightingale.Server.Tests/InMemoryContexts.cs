using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>Makes contexts over one in-memory database of the gateway's own tables, a database per instance, so a store is tested without a server.</summary>
internal sealed class InMemoryContexts : IDbContextFactory<NightingaleDbContext>
{
    private readonly DbContextOptions<NightingaleDbContext> _options = new DbContextOptionsBuilder<NightingaleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;

    /// <inheritdoc/>
    public NightingaleDbContext CreateDbContext() => new(_options, new NightingaleSchema(NightingaleSchema.Default));

    /// <summary>Adds rows and saves them, as what was there before the test.</summary>
    public async Task SeedAsync(params object[] rows)
    {
        await using var context = CreateDbContext();
        context.AddRange(rows);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
