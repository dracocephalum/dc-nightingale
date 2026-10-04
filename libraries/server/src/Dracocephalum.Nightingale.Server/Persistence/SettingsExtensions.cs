using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// Settings as rows, and rows as settings. A settings object is written as one row per value,
/// named by its configuration path, and read back by the configuration binder, the same one that
/// turns <c>appsettings.json</c> into options. So what a store was initialized with binds into
/// the type the configuration binds into, and the two are compared as two instances of one type.
/// The binder's defaults suit a store older or newer than the server reading it: a setting with
/// no row takes its default, and a row for a setting the type does not have is ignored.
/// </summary>
public static class SettingsExtensions
{
    private static readonly JsonSerializerOptions Flattening = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Reads the settings rows into a settings object.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The settings; all defaults when there are no rows.</returns>
    public static async Task<T> ReadSettingsAsync<T>(this NightingaleDbContext context, CancellationToken cancellationToken)
        where T : INightingaleSettings, new()
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = await context.Settings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return Bind<T>(rows.Select(row => new KeyValuePair<string, string?>(row.Name, row.Value)));
    }

    /// <summary>Writes a settings object as rows, replacing the value of a row that exists.</summary>
    /// <param name="context">The context.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    public static Task WriteSettingsAsync(this NightingaleDbContext context, INightingaleSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        return WriteAsync(context, Flatten(settings), cancellationToken);
    }

    /// <summary>Writes one setting's row, replacing its value when the row exists.</summary>
    /// <param name="context">The context.</param>
    /// <param name="name">The setting's name, a configuration path.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public static Task WriteSettingAsync(this NightingaleDbContext context, string name, string? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return WriteAsync(context, [new(name, value)], cancellationToken);
    }

    /// <summary>Binds names and values into a settings object, as configuration does.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="pairs">The names, configuration paths, and their values.</param>
    /// <returns>The settings.</returns>
    public static T Bind<T>(IEnumerable<KeyValuePair<string, string?>> pairs)
        where T : INightingaleSettings, new()
    {
        var settings = new T();
        new ConfigurationBuilder().AddInMemoryCollection(pairs).Build().Bind(settings);
        return settings;
    }

    /// <summary>A settings object as names and values: one pair per value, named by its configuration path; a null value has no pair.</summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The pairs.</returns>
    public static IReadOnlyList<KeyValuePair<string, string?>> Flatten(INightingaleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var pairs = new List<KeyValuePair<string, string?>>();
        Flatten(JsonSerializer.SerializeToNode(settings, settings.GetType(), Flattening), string.Empty, pairs);
        return pairs;
    }

    private static async Task WriteAsync(NightingaleDbContext context, IReadOnlyList<KeyValuePair<string, string?>> pairs, CancellationToken cancellationToken)
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
                    Flatten(value, path.Length == 0 ? name : path + ConfigurationPath.KeyDelimiter + name, pairs);
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
