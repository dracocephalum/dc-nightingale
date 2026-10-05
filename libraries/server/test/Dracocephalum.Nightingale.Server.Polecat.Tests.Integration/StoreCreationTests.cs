using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The tests that are not about initialization make their store from the event store's creation
/// script, <see cref="TestDatabases.ProvisionAsync"/>, because the server's own way, comparing the
/// database with what the store expects, costs many seconds per new database. That is only
/// honest while the two arrive at the same place. So one database is made each way and they are
/// read side by side: every column, index, constraint, module, type and partition function, by
/// definition, and the rows that record what the store was made from.
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

    /// <summary>What the store says it was made from; when and by whom are the two rows that differ by design.</summary>
    private const string Rows = "SELECT CONCAT([Name], ' = ', [Value]) FROM nightingale.StoreProperty WHERE [Name] NOT IN ('CreatedAt', 'CreatedBy') ORDER BY [Name]";

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
        // Arrange: one database the way the tests make a store, from the script.
        await TestDatabases.ProvisionAsync(_fromScript, options => options.Store.Partitioning = partitioning);

        // Act: another the way the server makes one, by comparison, here from nothing, read as
        // the server left it. Then an append, which under tenant partitioning provisions the
        // tenant's partition and sequence, and must leave the tables what the store expects.
        List<string> fromComparison;
        List<string> rowsFromComparison;
        using (var host = await TestDatabases.StartHostAsync(_fromComparison, options => options.Store.Partitioning = partitioning))
        {
            fromComparison = await LinesAsync(_fromComparison, Catalog);
            rowsFromComparison = await LinesAsync(_fromComparison, Rows);
            await host.Store().AppendAsync("orders-1", StreamState.NoStream, [new EventData(Guid.NewGuid(), "order_placed", "{}"u8.ToArray())], TestContext.Current.CancellationToken);
            var store = host.Services.GetRequiredService<global::Polecat.IDocumentStore>();
            var databases = await store.Options.Tenancy!.BuildDatabasesAsync(TestContext.Current.CancellationToken);
            await Should.NotThrowAsync(() => databases[0].AssertDatabaseMatchesConfigurationAsync(TestContext.Current.CancellationToken));
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        var fromScript = await LinesAsync(_fromScript, Catalog);
        (await LinesAsync(_fromScript, Rows)).ShouldBe(rowsFromComparison);
        fromScript.ShouldContain(line => line.StartsWith("column dbo.pc_events.seq_id ", StringComparison.Ordinal));
        fromScript.Except(fromComparison, StringComparer.Ordinal).ShouldBeEmpty("only the database made from the script has these");
        fromComparison.Except(fromScript, StringComparer.Ordinal).ShouldBeEmpty("only the database made by comparison has these");
    }

    private static async Task<List<string>> LinesAsync(string name, string query)
    {
        await using var connection = new SqlConnection(TestDatabases.ConnectionStringFor(name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }
}
