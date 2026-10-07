using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Works out the connection string the read-only connection uses. With a name configured, the
/// string under that name exactly as written; without one, the main string with its application
/// intent set to read-only, which an availability-group listener with read-only routing, or a
/// managed database with read scale-out, sends to a readable secondary, and which every other
/// server ignores.
/// </summary>
internal static class ReadOnlyConnection
{
    /// <summary>Resolves the read-only connection string.</summary>
    /// <param name="connectionString">The main connection string.</param>
    /// <param name="options">The options.</param>
    /// <param name="namedConnectionStrings">Looks a connection string up by name; <see langword="null"/> when the host gave none to look in.</param>
    /// <param name="explicitConnectionString">A read-only connection string the host passed directly, which wins over the name.</param>
    /// <returns>The connection string, or <see langword="null"/> when the read-only connection is off.</returns>
    /// <exception cref="InvalidOperationException">A name is configured and no connection string goes by it.</exception>
    public static string? Resolve(string connectionString, NightingaleOptions options, Func<string, string?>? namedConnectionStrings, string? explicitConnectionString = null)
    {
        if (!options.UseReadOnlyConnection)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            return explicitConnectionString;
        }

        if (!string.IsNullOrWhiteSpace(options.ReadOnlyConnectionStringName))
        {
            var found = namedConnectionStrings?.Invoke(options.ReadOnlyConnectionStringName);
            return string.IsNullOrWhiteSpace(found)
                ? throw new InvalidOperationException($"No connection string named '{options.ReadOnlyConnectionStringName}' is configured under ConnectionStrings; {NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptions.ReadOnlyConnectionStringName)} names it.")
                : found;
        }

        return new SqlConnectionStringBuilder(connectionString) { ApplicationIntent = ApplicationIntent.ReadOnly }.ConnectionString;
    }
}
