using Dracocephalum.Nightingale.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The context's model and the schema feature describe the same tables. The feature creates
/// them and the context reads and writes them, so the two are held equal here, column for
/// column: name, type, nullability and key. Building the model needs the provider's type
/// mappings, not a connection, so the design-time placeholder string is enough.
/// </summary>
public sealed class NightingaleDbContextTests
{
    [Fact]
    public void Model_ShouldMirrorEveryTableTheSchemaFeatureDeclares()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<NightingaleDbContext>()
            .UseSqlServer("Server=localhost;Database=design-time;Integrated Security=true")
            .Options;
        using var context = new NightingaleDbContext(options, new NightingaleTables("dbo"));
        var declared = new NightingaleTablesFeature("dbo").Objects.OfType<Table>().ToList();

        // Act
        var mapped = context.Model.GetEntityTypes().Select(entity => new
        {
            Schema = entity.GetSchema() ?? context.Model.GetDefaultSchema(),
            Name = entity.GetTableName(),
            Columns = entity.GetProperties()
                .Select(property => (property.GetColumnName(), property.GetColumnType(), property.IsNullable))
                .OrderBy(column => column.Item1, StringComparer.Ordinal)
                .ToList(),
            Key = entity.FindPrimaryKey()!.Properties.Select(property => property.GetColumnName()).ToList(),
        }).OrderBy(table => table.Name, StringComparer.Ordinal).ToList();

        // Assert
        declared.Count.ShouldBe(6);
        mapped.Count.ShouldBe(declared.Count);
        foreach (var table in declared)
        {
            var entity = mapped.SingleOrDefault(candidate => candidate.Name == table.Identifier.Name).ShouldNotBeNull($"{table.Identifier.Name} is declared but not mapped");
            entity.Schema.ShouldBe(table.Identifier.Schema);
            entity.Columns.ShouldBe(
                table.Columns.Select(column => (column.Name, column.Type, column.AllowNulls)).OrderBy(column => column.Name, StringComparer.Ordinal).ToList(),
                $"{table.Identifier.Name}: the columns differ");
            entity.Key.ShouldBe(table.PrimaryKeyColumns.ToList(), $"{table.Identifier.Name}: the key differs");
        }
    }
}
