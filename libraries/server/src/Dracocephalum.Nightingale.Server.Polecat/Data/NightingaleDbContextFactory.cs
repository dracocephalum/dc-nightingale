using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dracocephalum.Nightingale.Server.Polecat.Data;

/// <summary>
/// How the gateway's context is configured for SQL Server, at run time and at design time alike:
/// this backend's assembly holds the migrations, their history sits in the gateway's schema,
/// and the migrations are retargeted to that schema as they run. The design-time factory is what
/// <c>dotnet ef migrations add</c> uses; its connection string is a placeholder nothing opens,
/// and its schema is the one migrations are generated under.
/// </summary>
internal sealed class NightingaleDbContextFactory : IDesignTimeDbContextFactory<NightingaleDbContext>
{
    private const string DesignTime = "Server=localhost;Database=design-time;Integrated Security=true";

    /// <summary>Configures the context's options for SQL Server.</summary>
    /// <param name="builder">The options builder.</param>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="schema">The schema the gateway's tables live in.</param>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString, string schema)
    {
        builder
            .UseSqlServer(connectionString, sql => sql
                .MigrationsAssembly(typeof(NightingaleDbContextFactory).Assembly.GetName().Name)
                .MigrationsHistoryTable(NightingaleSchema.HistoryTable, schema))
            .ReplaceService<IMigrationsSqlGenerator, SchemaAwareSqlServerMigrationsSqlGenerator>();

        // Before it migrates, the provider compares the model with the migrations' snapshot and
        // refuses on a difference. Under another schema the two differ in exactly that, the
        // schema, which is the difference the generator above carries; the comparison that
        // matters, under the schema the migrations were generated in, is a unit test.
        if (!string.Equals(schema, NightingaleSchema.Default, StringComparison.Ordinal))
        {
            builder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        }
    }

    /// <inheritdoc/>
    public NightingaleDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<NightingaleDbContext>();
        Configure(builder, DesignTime, NightingaleSchema.Default);
        return new NightingaleDbContext(builder.Options, new NightingaleSchema(NightingaleSchema.Default));
    }
}
