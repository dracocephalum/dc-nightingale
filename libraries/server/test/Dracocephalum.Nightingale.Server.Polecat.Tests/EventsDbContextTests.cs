using Dracocephalum.Nightingale.Server.Polecat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Shouldly;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The mirror of the store's events table against the store's own definition of it. The store
/// owns the table, so the mirror maps the columns it reads and no more; every one it maps is
/// held here to the store's column of that name, type and nullability, and the keys to the
/// store's. Building either side needs no connection.
/// </summary>
public sealed class EventsDbContextTests
{
    private const string DesignTime = "Server=example;Database=nightingale;Trusted_Connection=True";

    [Fact]
    public async Task Model_ShouldMapEveryColumnAsTheStoreDeclaresIt()
    {
        // Arrange: the store as a host registers it, with ordinals, so every mapped column exists.
        var services = new ServiceCollection();
        services.AddNightingalePolecat(DesignTime, options => options.Store.AssignOrdinals = true);
        await using var provider = services.BuildServiceProvider();
        var databases = await provider.GetRequiredService<IDocumentStore>().Options.Tenancy!.BuildDatabasesAsync(TestContext.Current.CancellationToken);
        var declared = databases[0].BuildFeatureSchemas().SelectMany(feature => feature.Objects).OfType<Table>().ToList();
        using var context = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, "dbo");

        // Act & Assert
        var entities = context.Model.GetEntityTypes().ToList();
        entities.Count.ShouldBe(2);
        foreach (var entity in entities)
        {
            var table = declared.SingleOrDefault(candidate => candidate.Identifier.Name == entity.GetTableName()).ShouldNotBeNull($"{entity.GetTableName()} is mapped but the store declares no such table");
            (entity.GetSchema() ?? context.Model.GetDefaultSchema()).ShouldBe(table.Identifier.Schema);
            entity.FindPrimaryKey()!.Properties.Select(property => property.GetColumnName()).ShouldBe(table.PrimaryKeyColumns.ToList(), $"{table.Identifier.Name}: the key differs");
            foreach (var property in entity.GetProperties())
            {
                var column = table.Columns.SingleOrDefault(candidate => candidate.Name == property.GetColumnName()).ShouldNotBeNull($"{table.Identifier.Name}.{property.GetColumnName()} is mapped but the store declares no such column");
                property.GetColumnType().ShouldBe(column.Type, $"{table.Identifier.Name}.{column.Name}: the type differs");
                if (column.Name != "category")
                {
                    // The category is computed from a column that is never null; the store's
                    // definition of a computed column says nothing of nullability.
                    property.IsNullable.ShouldBe(column.AllowNulls, $"{table.Identifier.Name}.{column.Name}: the nullability differs");
                }
            }
        }
    }

    [Fact]
    public void Query_ShouldSendTheKeyAsTheColumnsOwnType()
    {
        // Arrange: a name sent as Unicode to a column that is not makes the database convert the
        // column and scan the index it should seek.
        using var context = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, "dbo");
        var tenant = "tenant";
        var key = "orders";

        // Act
        var byCategory = context.Events.Where(row => row.TenantId == tenant && row.Category == key).Select(row => row.SeqId).ToQueryString();
        var byType = context.Events.Where(row => row.TenantId == tenant && row.Type == key).Select(row => row.SeqId).ToQueryString();
        var mark = context.Progression.Where(row => row.Name == EventsDbContext.HighWaterMark).Select(row => row.LastSeqId).ToQueryString();

        // Assert
        byCategory.ShouldContain("varchar(250)");
        byType.ShouldContain("varchar(500)");
        foreach (var sql in new[] { byCategory, byType, mark })
        {
            sql.ShouldNotContain("nvarchar");
            sql.ShouldNotContain("N'");
        }
    }
}
