using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Polecat.Data;
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
        using var context = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo"));

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
    public void Sets_ShouldExposeEveryMappedTableUnderItsEntitysName()
    {
        // Arrange: the mirror keeps the owner's table and column names, so the model conventions
        // are not its to follow; how it names its own sets is.
        using var main = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo"));
        using var readOnly = new ReadOnlyEventsDbContext(new DbContextOptionsBuilder<ReadOnlyEventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo"));

        // Act & Assert
        Should.NotThrow(() => ModelConventions.CheckSets(main));
        Should.NotThrow(() => ModelConventions.CheckSets(readOnly));
    }

    [Fact]
    public void SaveChanges_ShouldRefuse()
    {
        // Arrange: the mirror reads a table the store owns; a write through it is always a mistake.
        using var context = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseInMemoryDatabase("mirror-" + Guid.NewGuid().ToString("N")).Options, new EventStoreSchema("dbo"));
        context.ProgressionRows.Add(new ProgressionRow { Name = EventsDbContext.HighWaterMark, LastSeqId = 1 });

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => context.SaveChanges());
    }

    [Fact]
    public void ReadOnlyMirror_ShouldMapTheSameTablesUnderItsOwnType()
    {
        // Arrange: one mapping, two types, so each connection has a registration of its own.
        using var main = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo"));
        using var readOnly = new ReadOnlyEventsDbContext(new DbContextOptionsBuilder<ReadOnlyEventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo"));

        // Act & Assert
        readOnly.Model.GetEntityTypes().Select(entity => entity.GetTableName()).ShouldBe(main.Model.GetEntityTypes().Select(entity => entity.GetTableName()));
        readOnly.EventRows.Where(row => row.SeqId > 1).Select(row => row.SeqId).ToQueryString().ShouldBe(main.EventRows.Where(row => row.SeqId > 1).Select(row => row.SeqId).ToQueryString());
    }

    [Fact]
    public void Query_ShouldSendTheKeyAsTheColumnsOwnType()
    {
        // Arrange: a name sent as Unicode to a column that is not makes the database convert the
        // column and scan the index it should seek.
        using var context = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo"));
        var tenant = "tenant";
        var key = "orders";

        // Act
        var byCategory = context.EventRows.Where(row => row.TenantId == tenant && row.Category == key).Select(row => row.SeqId).ToQueryString();
        var byType = context.EventRows.Where(row => row.TenantId == tenant && row.Type == key).Select(row => row.SeqId).ToQueryString();
        var mark = context.ProgressionRows.Where(row => row.Name == EventsDbContext.HighWaterMark).Select(row => row.LastSeqId).ToQueryString();

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
