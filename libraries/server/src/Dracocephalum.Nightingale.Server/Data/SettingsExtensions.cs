using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// The store's settings as rows, and the rows as settings. The settings are written as one row
/// per value, named by its configuration path, <c>Store:Partitioning</c>, the path it has in a
/// settings file under the gateway's section, and read back by the configuration binder, the
/// same one that turns <c>appsettings.json</c> into options. So what a store was initialized
/// with binds into the type the configuration binds into, and the two are compared through one
/// interface. The binder's defaults suit a store older or newer than the server reading it: a
/// setting with no row takes its default, and a row for a setting the type does not have is
/// ignored. Beside the settings the table holds what is known about the store and is no setting,
/// who initialized it and when, each a row of its own read and written by name.
/// </summary>
public static class SettingsExtensions
{
    /// <summary>The configuration section the store's settings are in, and the prefix of their rows' names.</summary>
    public const string StoreSection = "Store";

    /// <summary>Reads the store's settings from their rows.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The settings; all defaults when there are no rows.</returns>
    public static async Task<T> ReadSettingsAsync<T>(this NightingaleDbContext context, CancellationToken cancellationToken)
        where T : class, IStoreSettings, new()
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = await context.Settings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return Bind<T>(rows.Select(row => new KeyValuePair<string, string?>(row.Name, row.Value)));
    }

    /// <summary>Writes the store's settings as rows, replacing the value of a row that exists.</summary>
    /// <param name="context">The context.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    public static Task WriteSettingsAsync(this NightingaleDbContext context, IStoreSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        return WriteAsync(context, Flatten(settings), cancellationToken);
    }

    /// <summary>Reads one row's value by name.</summary>
    /// <param name="context">The context.</param>
    /// <param name="name">The row's name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The value, or <see langword="null"/> when there is no such row.</returns>
    public static async Task<string?> ReadSettingAsync(this NightingaleDbContext context, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return await context.Settings.AsNoTracking()
            .Where(row => row.Name == name)
            .Select(row => row.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Writes one row, replacing its value when the row exists.</summary>
    /// <param name="context">The context.</param>
    /// <param name="name">The row's name.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public static Task WriteSettingAsync(this NightingaleDbContext context, string name, string? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return WriteAsync(context, [new(name, value)], cancellationToken);
    }

    /// <summary>Binds names and values into the store's settings, as configuration does.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="pairs">The names, configuration paths, and their values.</param>
    /// <returns>The settings.</returns>
    public static T Bind<T>(IEnumerable<KeyValuePair<string, string?>> pairs)
        where T : class, IStoreSettings, new()
    {
        var settings = new T();
        new ConfigurationBuilder().AddInMemoryCollection(pairs).Build().GetSection(StoreSection).Bind(settings);
        return settings;
    }

    /// <summary>
    /// The store's settings as names and values: one pair per value, named by its configuration
    /// path; a null value has no pair. Only what <see cref="IStoreSettings"/> declares is written,
    /// whatever else the object carries for its host.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The pairs.</returns>
    public static List<KeyValuePair<string, string?>> Flatten(IStoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var pairs = new List<KeyValuePair<string, string?>>();
        Flatten(JsonSerializer.SerializeToNode<IStoreSettings>(settings, NightingaleJson.PascalCase), StoreSection, pairs);
        return pairs;
    }

    private static async Task WriteAsync(NightingaleDbContext context, List<KeyValuePair<string, string?>> pairs, CancellationToken cancellationToken)
    {
        var existing = await context.Settings.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (name, value) in pairs)
        {
            var row = existing.SingleOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                context.Settings.Add(new Setting { Name = name, Value = value });
            }
            else
            {
                row.Value = value;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Flatten(JsonNode? node, string path, List<KeyValuePair<string, string?>> pairs)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var (name, value) in members)
                {
                    Flatten(value, path + ConfigurationPath.KeyDelimiter + name, pairs);
                }

                break;
            case JsonArray items:
                for (var i = 0; i < items.Count; i++)
                {
                    Flatten(items[i], path + ConfigurationPath.KeyDelimiter + i.ToString(CultureInfo.InvariantCulture), pairs);
                }

                break;
            case JsonValue value:
                pairs.Add(new(path, value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString()));
                break;
            default:
                break;
        }
    }
}
