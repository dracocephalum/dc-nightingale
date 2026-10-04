using System.Collections;
using System.Reflection;

using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// Moves a migration's operations from the schema they were generated under to the one the host
/// named. Generated migrations carry a schema name as a literal on every operation; a host may
/// name another, so the operations are retargeted as they run and the generated code is never
/// edited. A backend's migrations SQL generator calls this before it writes any SQL. The one
/// thing it cannot reach is the text of a raw SQL operation, so a migration that needs raw SQL
/// must not name the schema in it.
/// </summary>
public static class MigrationSchema
{
    private static readonly string[] SchemaProperties = ["Schema", "NewSchema", "PrincipalSchema"];

    /// <summary>Retargets every operation, and every operation nested in one.</summary>
    /// <param name="operations">The operations.</param>
    /// <param name="generatedUnder">The schema the migrations were generated under.</param>
    /// <param name="target">The schema to run them in.</param>
    public static void Retarget(IEnumerable<MigrationOperation> operations, string generatedUnder, string target)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedUnder);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (string.Equals(generatedUnder, target, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var operation in operations)
        {
            Retarget(operation, generatedUnder, target);
        }
    }

    private static void Retarget(MigrationOperation operation, string generatedUnder, string target)
    {
        // Creating the schema is an operation of its own, and names the schema as its name.
        if (operation is EnsureSchemaOperation ensure)
        {
            if (ensure.Name == generatedUnder)
            {
                ensure.Name = target;
            }

            return;
        }

        foreach (var property in operation.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (property.PropertyType == typeof(string))
            {
                if (property.CanWrite && Array.IndexOf(SchemaProperties, property.Name) >= 0 && (string?)property.GetValue(operation) == generatedUnder)
                {
                    property.SetValue(operation, target);
                }
            }
            else if (property.GetValue(operation) is MigrationOperation nested)
            {
                Retarget(nested, generatedUnder, target);
            }
            else if (property.GetValue(operation) is IEnumerable several and not string)
            {
                foreach (var item in several.OfType<MigrationOperation>())
                {
                    Retarget(item, generatedUnder, target);
                }
            }
        }
    }
}
