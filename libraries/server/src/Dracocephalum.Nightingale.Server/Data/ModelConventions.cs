using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// The conventions a context that owns its tables follows, checked rather than patched: a test
/// builds the model and this throws with every violation, so a forgotten line fails the build.
/// The rules and their reasons are in the repository's EF Core rules.
/// </summary>
public static class ModelConventions
{
    private static readonly string[] MoneySuffixes = ["Amount", "Price", "Total", "Cost", "Fee", "Balance"];

    /// <summary>Checks a context: its model, and the sets it exposes its tables through.</summary>
    /// <param name="context">The context.</param>
    /// <param name="externalReferences">The columns that refer to a row of a table outside the model, each as <c>Entity.Property</c>.</param>
    /// <exception cref="InvalidOperationException">The context breaks a convention; the message lists every violation.</exception>
    public static void Check(DbContext context, params string[] externalReferences)
    {
        ArgumentNullException.ThrowIfNull(context);
        Check(context.Model, externalReferences);
        CheckSets(context);
    }

    /// <summary>
    /// Checks that every table a context maps is exposed through a set, and that a set is named
    /// for its entity: the class name, or its plural where it has one. A table with no set is
    /// reachable only through <c>Set&lt;T&gt;()</c>, which a reader of the context does not see;
    /// a set under another name is a second name for the same thing. This half applies to a
    /// context that mirrors another owner's tables too.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <exception cref="InvalidOperationException">A table has no set, or a set is not named for its entity; the message lists every violation.</exception>
    public static void CheckSets(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var violations = new List<string>();
        var sets = context.GetType().GetProperties()
            .Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .ToList();
        foreach (var entity in context.Model.GetEntityTypes().Where(e => !e.IsOwned()))
        {
            var name = entity.ClrType.Name;
            var own = sets.Where(set => set.PropertyType.GetGenericArguments()[0] == entity.ClrType).ToList();
            if (own.Count == 0)
            {
                violations.Add($"{name}: the context has no DbSet<{name}> - every table it maps is exposed, as '{name}' or its plural");
            }

            foreach (var set in own.Where(set => !IsNameOrPlural(set.Name, name)))
            {
                violations.Add($"{context.GetType().Name}.{set.Name}: a set is named for its entity - '{name}' or its plural");
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException("Model conventions violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }
    }

    /// <summary>Checks a model.</summary>
    /// <param name="model">The model.</param>
    /// <param name="externalReferences">
    /// The columns that refer to a row of a table outside the model, each as <c>Entity.Property</c>.
    /// Such a column is named like a foreign key and cannot be one: the table is another owner's.
    /// </param>
    /// <exception cref="InvalidOperationException">The model breaks a convention; the message lists every violation.</exception>
    public static void Check(IModel model, params string[] externalReferences)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(externalReferences);
        var violations = new List<string>();
        var tables = model.GetEntityTypes().Where(e => !e.IsOwned()).Select(e => e.GetTableName()).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in model.GetEntityTypes().Where(e => !e.IsOwned()))
        {
            var name = entity.DisplayName();
            if (entity.BaseType is null)
            {
                if (entity.GetTableName() != entity.ClrType.Name)
                {
                    violations.Add($"{name}: table must be '{entity.ClrType.Name}' - .ToTable(\"{entity.ClrType.Name}\")");
                }

                var key = entity.FindPrimaryKey();
                if (key is null || key.Properties.Count != 1 || key.Properties[0].Name != "Id" || GetStoreType(key.Properties[0]) != typeof(Guid))
                {
                    violations.Add($"{name}: primary key must be a single Guid column named Id");
                }
            }

            foreach (var fk in entity.GetForeignKeys().Where(fk => !fk.IsOwnership))
            {
                var principal = fk.PrincipalEntityType.GetTableName();
                if (fk.DeleteBehavior is not (DeleteBehavior.Restrict or DeleteBehavior.NoAction))
                {
                    violations.Add($"{name} -> {fk.PrincipalEntityType.DisplayName()}: {fk.DeleteBehavior} - .OnDelete(DeleteBehavior.Restrict)");
                }

                if (fk.Properties.Count != 1 || fk.Properties[0].Name != principal + "Id")
                {
                    violations.Add($"{name} -> {fk.PrincipalEntityType.DisplayName()}: the foreign key column must be '{principal}Id', the table it refers to and Id");
                }
            }

            foreach (var property in entity.GetProperties())
            {
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clr.IsEnum && (GetStoreType(property) != typeof(string) || property.GetMaxLength() is null))
                {
                    violations.Add($"{name}.{property.Name}: enum must be a bounded string - .HasConversion<string>().HasMaxLength(64)");
                }

                if (clr == typeof(decimal) && MoneySuffixes.Any(s => property.Name.EndsWith(s, StringComparison.Ordinal)) && property.GetPrecision() is null)
                {
                    violations.Add($"{name}.{property.Name}: money needs an explicit precision - .HasPrecision(19, 4), or the agreed crypto precision");
                }

                // A Guid column named <Something>Id says it refers to a row of <Something>. Either
                // that is a table of this model and the column is a foreign key to it, or the
                // table is another owner's and the column is listed as an external reference.
                if (clr == typeof(Guid) && property.Name != "Id" && property.Name.EndsWith("Id", StringComparison.Ordinal) && !property.IsForeignKey())
                {
                    var referred = property.Name[..^2];
                    if (tables.Contains(referred))
                    {
                        violations.Add($"{name}.{property.Name}: refers to {referred} by name and is not a foreign key to it - .HasOne<{referred}>().WithMany().HasForeignKey(...).OnDelete(DeleteBehavior.Restrict)");
                    }
                    else if (Array.IndexOf(externalReferences, $"{entity.ClrType.Name}.{property.Name}") < 0)
                    {
                        violations.Add($"{name}.{property.Name}: named like a reference to a table '{referred}' this model does not have - name it for the table it refers to, or list it as an external reference");
                    }
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException("Model conventions violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }
    }

    /// <summary>The name itself, for a word with no plural, or one of its regular plurals.</summary>
    private static bool IsNameOrPlural(string candidate, string name) =>
        candidate == name
        || candidate == name + "s"
        || candidate == name + "es"
        || (name.EndsWith('y') && candidate == name[..^1] + "ies");

    private static Type GetStoreType(IProperty property) => property.GetTypeMapping().Converter?.ProviderClrType ?? property.ClrType;
}
