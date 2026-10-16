using System.Data.Common;

using Dracocephalum.Nightingale.Server.Contracts;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The SQL Server horizon by partition over the store's own tables partitioned by tenant, against
/// the contract every horizon by partition keeps: two tenants in partitions of their own, and a
/// writer that writes its stream's row, the first thing an append does.
/// </summary>
/// <param name="store">The store's tables, made once for the class.</param>
public sealed class SqlServerTenantPartitionHorizonTests(SqlServerTenantPartitionHorizonTests.TenantStore store) : PartitionHorizonContract, IClassFixture<SqlServerTenantPartitionHorizonTests.TenantStore>
{
    /// <inheritdoc/>
    protected override ITransactionHorizon Horizon { get; } = SqlServerPartitionHorizon.OverStore(TestDatabases.ConnectionStringFor(store.Name), "dbo", partitionedByTenant: true);

    /// <inheritdoc/>
    protected override SequenceKey First => new(TenantStore.Acme);

    /// <inheritdoc/>
    protected override SequenceKey Second => new(TenantStore.Globex);

    /// <inheritdoc/>
    protected override async Task<DbTransaction> BeginWriteAsync(SequenceKey sequence, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(TestDatabases.ConnectionStringFor(store.Name));
        await connection.OpenAsync(cancellationToken);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = new SqlCommand("INSERT INTO dbo.pc_streams (tenant_id, id, tenant_ordinal, version) VALUES (@tenant, @id, @ordinal, 1)", connection, transaction);
        insert.Parameters.AddWithValue("@tenant", sequence.TenantId);
        insert.Parameters.AddWithValue("@id", "orders-" + Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("@ordinal", sequence.TenantId == TenantStore.Acme ? 1 : 2);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    /// <summary>
    /// The store's tables partitioned by tenant, with two tenants in partitions of their own as the
    /// store provisions them: a partition key each in its registry, a split of each table's
    /// partition function at it. Dropped after the class.
    /// </summary>
    public sealed class TenantStore : IAsyncLifetime
    {
        /// <summary>The tenant in the first partition.</summary>
        public const string Acme = "acme";

        /// <summary>The tenant in the second partition.</summary>
        public const string Globex = "globex";

        /// <summary>Gets the database's name.</summary>
        public string Name { get; } = TestDatabases.NewName();

        public async ValueTask InitializeAsync()
        {
            await TestDatabases.CreateEmptyAsync(Name);
            await TestDatabases.CreateStoreTablesAsync(Name, options => options.Store.Partitioning = PartitioningMode.Tenant);
            foreach (var table in new[] { "pc_events", "pc_streams" })
            {
                foreach (var key in new[] { 1, 2 })
                {
                    await TestDatabases.ExecuteAsync(Name, $"ALTER PARTITION SCHEME ps_{table}_tenant_ordinal NEXT USED [PRIMARY]; ALTER PARTITION FUNCTION pf_{table}_tenant_ordinal() SPLIT RANGE ({key});");
                }
            }

            await TestDatabases.ExecuteAsync(Name, $"INSERT INTO dbo.pc_tenant_partitions (tenant_id, ordinal) VALUES ('{Acme}', 1), ('{Globex}', 2);");
        }

        public async ValueTask DisposeAsync() => await TestDatabases.DropAsync(Name);
    }
}
