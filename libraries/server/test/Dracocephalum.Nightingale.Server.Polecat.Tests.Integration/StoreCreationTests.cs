using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// An empty database is initialized from the event store's creation script; a database that
/// already holds a store is changed by the store's comparison of what is there with what it
/// expects. The two must arrive at the same place, or a store initialized by this server would
/// differ from one the store's own tooling made, in a way the comparison might not look at. So
/// one database is made each way and their catalogs are read side by side: every column, index,
/// constraint, module, type and partition function, by definition.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class StoreCreationTests : IAsyncLifetime
{
    /// <summary>
    /// One line per thing the catalog holds, written so that two databases with the same schema
    /// give the same lines. Default constraints are told by their definition, not their name,
    /// which the server makes up where the script gives none.
    /// </summary>
    private const string Catalog = """
        SELECT line FROM (
            SELECT CONCAT('column ', s.name, '.', t.name, '.', c.name, ' ', ty.name, '(', c.max_length, ',', c.precision, ',', c.scale, ')',
                ' nullable=', c.is_nullable, ' identity=', c.is_identity, ' collation=', c.collation_name,
                ' computed=', cc.definition, ' persisted=', cc.is_persisted, ' default=', dc.definition) AS line
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            UNION ALL
            SELECT CONCAT('index ', s.name, '.', t.name, '.', i.name, ' ', i.type_desc COLLATE DATABASE_DEFAULT, ' unique=', i.is_unique, ' primary=', i.is_primary_key,
                ' constraint=', i.is_unique_constraint, ' filter=', i.filter_definition, ' space=', ds.type_desc COLLATE DATABASE_DEFAULT, ' columns=',
                (SELECT STRING_AGG(CONCAT(col.name, CASE WHEN ic.is_descending_key = 1 THEN ' desc' ELSE '' END,
                        CASE WHEN ic.is_included_column = 1 THEN ' included' ELSE '' END, CASE WHEN ic.partition_ordinal > 0 THEN ' partitioning' ELSE '' END), ', ')
                    WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, col.name)
                 FROM sys.index_columns ic
                 JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                 WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id))
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            UNION ALL
            SELECT CONCAT('foreign key ', s.name, '.', t.name, '.', fk.name, ' references ', OBJECT_NAME(fk.referenced_object_id),
                ' delete=', fk.delete_referential_action_desc COLLATE DATABASE_DEFAULT, ' update=', fk.update_referential_action_desc COLLATE DATABASE_DEFAULT)
            FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            UNION ALL
            SELECT CONCAT('check ', s.name, '.', t.name, '.', ck.name, ' ', ck.definition)
            FROM sys.check_constraints ck
            JOIN sys.tables t ON t.object_id = ck.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            UNION ALL
            SELECT CONCAT('module ', s.name, '.', o.name, ' ', o.type_desc COLLATE DATABASE_DEFAULT, ' ', m.definition)
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            UNION ALL
            SELECT CONCAT('sequence ', s.name, '.', q.name, ' start=', CONVERT(bigint, q.start_value), ' increment=', CONVERT(bigint, q.increment))
            FROM sys.sequences q
            JOIN sys.schemas s ON s.schema_id = q.schema_id
            UNION ALL
            SELECT CONCAT('table type ', s.name, '.', tt.name, '.', c.name, ' ', ty.name, '(', c.max_length, ') nullable=', c.is_nullable)
            FROM sys.table_types tt
            JOIN sys.schemas s ON s.schema_id = tt.schema_id
            JOIN sys.columns c ON c.object_id = tt.type_table_object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            UNION ALL
            SELECT CONCAT('partition function ', pf.name, ' ', pf.type_desc COLLATE DATABASE_DEFAULT, ' fanout=', pf.fanout, ' right=', pf.boundary_value_on_right)
            FROM sys.partition_functions pf
            UNION ALL
            SELECT CONCAT('partition scheme ', ps.name, ' on ', pf.name)
            FROM sys.partition_schemes ps
            JOIN sys.partition_functions pf ON pf.function_id = ps.function_id
        ) AS catalog
        ORDER BY line
        """;

    private readonly string _fromScript = TestDatabases.NewName();
    private readonly string _fromComparison = TestDatabases.NewName();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await TestDatabases.DropAsync(_fromScript);
        await TestDatabases.DropAsync(_fromComparison);
    }

    [Theory]
    [InlineData(PartitioningMode.None)]
    [InlineData(PartitioningMode.Tenant)]
    [InlineData(PartitioningMode.ArchivedStream)]
    public async Task Create_ShouldLeaveWhatTheStoresOwnComparisonWouldHaveMade(PartitioningMode partitioning)
    {
        // Arrange: one database the way the server initializes an empty one, from the script.
        using (var host = await TestDatabases.StartHostAsync(_fromScript, options => options.Store.Partitioning = partitioning))
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // Act: another the way a store is changed, by comparison, here from nothing.
        await TestDatabases.CreateEmptyAsync(_fromComparison);
        var services = new ServiceCollection();
        services.AddNightingalePolecat(TestDatabases.ConnectionStringFor(_fromComparison), options => options.Store.Partitioning = partitioning);
        await using (var provider = services.BuildServiceProvider())
        {
            await provider.GetRequiredService<StoreSchema>().ApplyAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        var fromScript = await CatalogAsync(_fromScript);
        var fromComparison = await CatalogAsync(_fromComparison);
        fromScript.ShouldContain(line => line.StartsWith("column dbo.pc_events.seq_id ", StringComparison.Ordinal));
        fromScript.Except(fromComparison, StringComparer.Ordinal).ShouldBeEmpty("only the database made from the script has these");
        fromComparison.Except(fromScript, StringComparer.Ordinal).ShouldBeEmpty("only the database made by comparison has these");
    }

    private static async Task<List<string>> CatalogAsync(string name)
    {
        await using var connection = new SqlConnection(TestDatabases.ConnectionStringFor(name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Catalog;
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }
}
