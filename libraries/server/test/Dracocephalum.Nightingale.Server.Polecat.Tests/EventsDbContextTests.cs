using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
        // Arrange: the store as a host registers it, with ordinals, so every mapped column exists;
        // and the same partitioned by tenant, where the partition key and its registry exist. A
        // table or column is held to the plain store's declaration where it is declared there,
        // and to the partitioned one's otherwise; keys to the plain store's.
        var declared = await DeclareTablesAsync(options => options.Store.AssignOrdinals = true);
        var partitioned = await DeclareTablesAsync(options => options.Store.Partitioning = PartitioningMode.Tenant);
        using var context = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo"));

        // Act & Assert: the gateway's links are the next test's.
        var entities = context.Model.GetEntityTypes().Where(entity => entity.ClrType != typeof(OrdinalLink)).ToList();
        entities.Count.ShouldBe(4);
        foreach (var entity in entities)
        {
            var plain = declared.SingleOrDefault(candidate => candidate.Identifier.Name == entity.GetTableName());
            var table = (plain ?? partitioned.SingleOrDefault(candidate => candidate.Identifier.Name == entity.GetTableName())).ShouldNotBeNull($"{entity.GetTableName()} is mapped but the store declares no such table");
            var partitionedTable = partitioned.Single(candidate => candidate.Identifier.Name == entity.GetTableName());
            (entity.GetSchema() ?? context.Model.GetDefaultSchema()).ShouldBe(table.Identifier.Schema);
            entity.FindPrimaryKey()!.Properties.Select(property => property.GetColumnName()).ShouldBe(table.PrimaryKeyColumns.ToList(), $"{table.Identifier.Name}: the key differs");
            foreach (var property in entity.GetProperties())
            {
                var column = (table.Columns.SingleOrDefault(candidate => candidate.Name == property.GetColumnName())
                    ?? partitionedTable.Columns.SingleOrDefault(candidate => candidate.Name == property.GetColumnName())).ShouldNotBeNull($"{table.Identifier.Name}.{property.GetColumnName()} is mapped but the store declares no such column");
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
    public void Model_ShouldMapTheOrdinalLinksAsTheGatewaysOwnModelDeclaresThem()
    {
        // Arrange: the links are the gateway's table, created by its migrations in its schema; the
        // mirror reads them beside the events, so it is held to that model, in whatever schema
        // the host names.
        using var mirror = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo", "gateway"));
        var builder = new DbContextOptionsBuilder<NightingaleDbContext>();
        NightingaleDbContextFactory.Configure(builder, DesignTime, "gateway");
        using var gateway = new NightingaleDbContext(builder.Options, new NightingaleSchema("gateway"));
        var mapped = mirror.Model.FindEntityType(typeof(OrdinalLink)).ShouldNotBeNull();
        var declared = gateway.Model.FindEntityType(typeof(OrdinalLink)).ShouldNotBeNull();

        // Act & Assert
        mapped.GetTableName().ShouldBe(declared.GetTableName());
        mapped.GetSchema().ShouldBe("gateway");
        declared.GetSchema().ShouldBe("gateway");
        mapped.FindPrimaryKey()!.Properties.Select(property => property.GetColumnName()).ShouldBe(declared.FindPrimaryKey()!.Properties.Select(property => property.GetColumnName()));
        foreach (var property in mapped.GetProperties())
        {
            var other = declared.FindProperty(property.Name).ShouldNotBeNull($"OrdinalLink.{property.Name} is mapped but the gateway declares no such column");
            property.GetColumnName().ShouldBe(other.GetColumnName());
            property.GetColumnType().ShouldBe(other.GetColumnType(), $"OrdinalLink.{property.Name}: the type differs");
            property.IsNullable.ShouldBe(other.IsNullable, $"OrdinalLink.{property.Name}: the nullability differs");
        }
    }

    [Fact]
    public void Model_OfMirrorsOverTwoGatewaySchemas_ShouldBeTwoModels()
    {
        // Arrange: one store schema, two gateway schemas, in one process, as tests have them; a
        // model cached under the store's schema alone would read the first one's links for both.
        using var first = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo", "first"));
        using var second = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo", "second"));

        // Act & Assert
        first.Model.FindEntityType(typeof(OrdinalLink))!.GetSchema().ShouldBe("first");
        second.Model.FindEntityType(typeof(OrdinalLink))!.GetSchema().ShouldBe("second");
    }

    [Fact]
    public void Query_OfTheLinks_ShouldSendTheKeyAsTheColumnsOwnTypeAndNameThePartitionKeyWherePartitioned()
    {
        // Arrange
        using var context = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo"));
        var partitioned = new VirtualStreamReader(() => context, "tenant", partitionedByTenant: true);
        var plain = new VirtualStreamReader(() => context, "tenant");

        // Act
        var onPartitions = partitioned.QueryLinked(context, OrdinalLinkType.Correlation, "checkout-1", Direction.Forwards, 0).ToQueryString();
        var onOne = plain.QueryLinked(context, OrdinalLinkType.Correlation, "checkout-1", Direction.Forwards, 0).ToQueryString();

        // Assert: the type a byte, the names as the columns type them, and on a store partitioned
        // by tenant each event fetched in the tenant's partition.
        onOne.ShouldContain("tinyint");
        onOne.ShouldContain("varchar(250)");
        onOne.ShouldContain("[OrdinalLink]");
        onOne.ShouldNotContain("nvarchar");
        onOne.ShouldNotContain("N'");
        onOne.ShouldNotContain("tenant_ordinal");
        onPartitions.ShouldContain("[tenant_ordinal] = (");
        onPartitions.ShouldContain("[pc_tenant_partitions]");
    }

    [Fact]
    public void Query_OnAStorePartitionedByTenant_ShouldNameThePartitionKeyInEveryStatementOfAStreamsRead()
    {
        // Arrange: the store's unique index of a stream's versions, and its streams' key, lead with
        // the partition key there; a statement that does not name it cannot seek them.
        using var context = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer(DesignTime).Options, new EventStoreSchema("dbo"));
        var partitioned = new PlainStreamReader(() => context, "tenant", partitionedByTenant: true, NullLogger.Instance);
        var plain = new PlainStreamReader(() => context, "tenant", partitionedByTenant: false, NullLogger.Instance);

        // Act
        var first = partitioned.FindWithMetadata(context, "orders-1", "$$orders-1").ToQueryString();
        var page = partitioned.QueryRows(context, "orders-1").Select(row => row.SeqId).ToQueryString();
        var plainFirst = plain.FindWithMetadata(context, "orders-1", "$$orders-1").ToQueryString();

        // Assert: both tables of the first statement, the streams and the probe, and the page.
        CountOf(first, "[tenant_ordinal] = (").ShouldBe(2, first + Environment.NewLine + page);
        first.ShouldContain("[pc_tenant_partitions]");
        page.ShouldContain("[tenant_ordinal] = (");
        plainFirst.ShouldNotContain("tenant_ordinal");
        plainFirst.ShouldNotContain("pc_tenant_partitions");
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
        var reader = new PlainStreamReader(() => context, tenant, partitionedByTenant: false, NullLogger.Instance);
        var stream = reader.FindWithMetadata(context, "orders-1", "$$orders-1").ToQueryString();
        var page = reader.QueryRows(context, "orders-1").Select(row => row.SeqId).ToQueryString();

        // Assert
        byCategory.ShouldContain("varchar(250)");
        byType.ShouldContain("varchar(500)");
        stream.ShouldContain("varchar(250)");
        page.ShouldContain("varchar(250)");
        foreach (var sql in new[] { byCategory, byType, mark, stream, page })
        {
            sql.ShouldNotContain("nvarchar");
            sql.ShouldNotContain("N'");
        }
    }

    private static async Task<List<Table>> DeclareTablesAsync(Action<NightingaleOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddNightingalePolecat(DesignTime, TestAuth.Off(configure));
        await using var provider = services.BuildServiceProvider();
        var databases = await provider.GetRequiredService<IDocumentStore>().Options.Tenancy!.BuildDatabasesAsync(TestContext.Current.CancellationToken);
        return databases[0].BuildFeatureSchemas().SelectMany(feature => feature.Objects).OfType<Table>().ToList();
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
