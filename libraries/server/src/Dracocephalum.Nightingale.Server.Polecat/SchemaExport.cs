using Polecat;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Writes the store's creation script, with the gateway's additions, to a file. The store's own
/// export goes through its plain database definition and would leave out the category column,
/// the two indexes and the marker table, so an operator who wants the script the server would
/// apply asks the default host for it: <c>--export-schema &lt;file&gt;</c>. Nothing is connected
/// to; the script is built from the definitions alone.
/// </summary>
public static class SchemaExport
{
    /// <summary>The command-line switch the default host answers.</summary>
    public const string Switch = "--export-schema";

    /// <summary>Finds the export switch and the file it names.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="path">The file to write, when the switch is present.</param>
    /// <returns>True when the switch is present.</returns>
    /// <exception cref="ArgumentException">The switch is present without a file.</exception>
    public static bool TryGetPath(string[] args, out string path)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = Array.IndexOf(args, Switch);
        if (index < 0)
        {
            path = string.Empty;
            return false;
        }

        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"{Switch} needs the file to write.", nameof(args));
        }

        path = args[index + 1];
        return true;
    }

    /// <summary>Writes the creation script of every database the store's tenancy knows.</summary>
    /// <param name="store">The store, registered through the gateway.</param>
    /// <param name="path">The file to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public static async Task ExportAsync(IDocumentStore store, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var databases = await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false);
        await databases[0].WriteCreationScriptToFileAsync(path, cancellationToken).ConfigureAwait(false);
    }
}
