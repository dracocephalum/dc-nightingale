using Dracocephalum.Nightingale.Server.Persistence;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;

namespace Dracocephalum.Nightingale.Server.Polecat.Persistence;

/// <summary>
/// SQL Server's migrations SQL generator, with one thing added: before it writes any SQL, the
/// operations are moved from the schema the migrations were generated under to the schema the
/// host named for the gateway's tables. That is what lets the schema be a setting while the
/// migrations stay exactly as the tool generated them.
/// </summary>
/// <param name="dependencies">The generator's dependencies.</param>
/// <param name="commandBatchPreparer">The command batch preparer.</param>
internal sealed class SchemaAwareSqlServerMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies, ICommandBatchPreparer commandBatchPreparer)
    : SqlServerMigrationsSqlGenerator(dependencies, commandBatchPreparer)
{
    /// <inheritdoc/>
    public override IReadOnlyList<MigrationCommand> Generate(IReadOnlyList<MigrationOperation> operations, IModel? model = null, MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        if (Dependencies.CurrentContext.Context is ISchemaScoped scoped)
        {
            MigrationSchema.Retarget(operations, NightingaleSchema.Default, scoped.Schema);
        }

        return base.Generate(operations, model, options);
    }
}
