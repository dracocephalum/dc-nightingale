using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// What is known about the store, as rows: a name and a value each, one level, no nesting. Some
/// are the settings the store was initialized with, <c>Partitioning</c>, <c>Collation</c>; the
/// rest are facts, who initialized it and when, which store library its tables are for. The
/// settings are written one row per value under the value's own name and read back by the
/// configuration binder, the one that turns <c>appsettings.json</c> into options, so what a
/// store was initialized with binds into the type the configuration binds into and the two are
/// compared through one interface. The binder's defaults suit a store older or newer than the
/// server reading it: a setting with no row takes its default, and a row the type has no member
/// for is ignored, which is also how the facts stay out of the settings. A setting is a value,
/// never an object or a list: a row's name is then the member's name and nothing more.
/// </summary>
public static class StorePropertyExtensions
{
    /// <summary>Reads the settings the store was initialized with from their rows.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The settings; all defaults when there are no rows.</returns>
    public static async Task<T> ReadStoreSettingsAsync<T>(this NightingaleDbContext context, CancellationToken cancellationToken)
        where T : class, IStoreSettings, new()
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = await context.StoreProperties.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return Bind<T>(rows.Select(row => new KeyValuePair<string, string?>(row.Name, row.Value)));
    }

    /// <summary>Writes the store's settings as rows, replacing the value of a row that exists.</summary>
    /// <param name="context">The context.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    public static Task WriteStoreSettingsAsync(this NightingaleDbContext context, IStoreSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        return WriteAsync(context, Flatten(settings), cancellationToken);
    }

    /// <summary>
    /// Writes the default tenant's row unless it is there: the store's own default id under a
    /// name, so a single-tenant store needs no tenant made and a credential bound to the default
    /// tenant is what today's callers are. Done when the schema is applied, after the
    /// migrations, which carry no data of their own.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="now">The clock's time, for the row's dates.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is there.</returns>
    public static async Task EnsureDefaultTenantAsync(this NightingaleDbContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await context.Tenants.AnyAsync(tenant => tenant.Id == Tenant.DefaultId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        context.Tenants.Add(new Tenant
        {
            Id = Tenant.DefaultId,
            Name = Tenant.DefaultName,
            StoreTenantId = Tenant.DefaultStoreTenantId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another instance wrote it first; the row is there either way.
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>Reads one property's value by name.</summary>
    /// <param name="context">The context.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The value, or <see langword="null"/> when there is no such row.</returns>
    public static async Task<string?> ReadStorePropertyAsync(this NightingaleDbContext context, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return await context.StoreProperties.AsNoTracking()
            .Where(row => row.Name == name)
            .Select(row => row.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Writes one property, replacing its value when the row exists.</summary>
    /// <param name="context">The context.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public static Task WriteStorePropertyAsync(this NightingaleDbContext context, string name, string? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return WriteAsync(context, [new(name, value)], cancellationToken);
    }

    /// <summary>Binds names and values into the store's settings, as configuration does.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="pairs">The names, each a member's, and their values.</param>
    /// <returns>The settings.</returns>
    public static T Bind<T>(IEnumerable<KeyValuePair<string, string?>> pairs)
        where T : class, IStoreSettings, new()
    {
        var settings = new T();
        new ConfigurationBuilder().AddInMemoryCollection(pairs).Build().Bind(settings);
        return settings;
    }

    /// <summary>
    /// The store's settings as names and values: one pair per value, named as the member is; a
    /// null value has no pair. Only what <see cref="IStoreSettings"/> declares is written,
    /// whatever else the object carries for its host.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The pairs.</returns>
    /// <exception cref="InvalidOperationException">A setting is an object or a list; the store's properties are one level of values.</exception>
    public static List<KeyValuePair<string, string?>> Flatten(IStoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var pairs = new List<KeyValuePair<string, string?>>();
        foreach (var (name, node) in JsonSerializer.SerializeToNode<IStoreSettings>(settings, NightingaleJson.PascalCase)!.AsObject())
        {
            switch (node)
            {
                case null:
                    break;
                case JsonValue value:
                    pairs.Add(new(name, value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString()));
                    break;
                default:
                    throw new InvalidOperationException($"The store setting {name} is not a single value. The store's properties are one level, a name and a value each; give the setting a member per value instead.");
            }
        }

        return pairs;
    }

    private static async Task WriteAsync(NightingaleDbContext context, List<KeyValuePair<string, string?>> pairs, CancellationToken cancellationToken)
    {
        var existing = await context.StoreProperties.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (name, value) in pairs)
        {
            var row = existing.SingleOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                context.StoreProperties.Add(new StoreProperty { Name = name, Value = value });
            }
            else
            {
                row.Value = value;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
