using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// The conventions a context that owns its tables follows, checked rather than patched: a test
/// builds the model and this throws with every violation, so a forgotten line fails the build.
/// The rules and their reasons are in the repository's EF Core rules.
/// </summary>
public static class ModelConventions
{
    private static readonly string[] MoneySuffixes = ["Amount", "Price", "Total", "Cost", "Fee", "Balance"];

    /// <summary>Checks a model.</summary>
    /// <param name="model">The model.</param>
    /// <exception cref="InvalidOperationException">The model breaks a convention; the message lists every violation.</exception>
    public static void Check(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var violations = new List<string>();
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
                if (key is null || key.Properties.Count != 1 || key.Properties[0].Name != "Id" || StoreType(key.Properties[0]) != typeof(Guid))
                {
                    violations.Add($"{name}: primary key must be a single Guid column named Id");
                }
            }

            foreach (var fk in entity.GetForeignKeys().Where(fk => !fk.IsOwnership && fk.DeleteBehavior is not (DeleteBehavior.Restrict or DeleteBehavior.NoAction)))
            {
                violations.Add($"{name} -> {fk.PrincipalEntityType.DisplayName()}: {fk.DeleteBehavior} - .OnDelete(DeleteBehavior.Restrict)");
            }

            foreach (var property in entity.GetProperties())
            {
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clr.IsEnum && (StoreType(property) != typeof(string) || property.GetMaxLength() is null))
                {
                    violations.Add($"{name}.{property.Name}: enum must be a bounded string - .HasConversion<string>().HasMaxLength(64)");
                }

                if (clr == typeof(decimal) && MoneySuffixes.Any(s => property.Name.EndsWith(s, StringComparison.Ordinal)) && property.GetPrecision() is null)
                {
                    violations.Add($"{name}.{property.Name}: money needs an explicit precision - .HasPrecision(19, 4), or the agreed crypto precision");
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException("Model conventions violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }
    }

    private static Type StoreType(IProperty property) => property.GetTypeMapping().Converter?.ProviderClrType ?? property.ClrType;
}
