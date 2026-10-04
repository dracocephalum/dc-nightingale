namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The comparison between what a store was initialized with and what a host is configured with.
/// It is written against the interface, so it holds for any two things that carry the settings,
/// whichever backend they are for.
/// </summary>
public static class StoreSettingsExtensions
{
    /// <summary>Finds where the configuration disagrees with what the store was initialized with.</summary>
    /// <param name="stored">What the store was initialized with; its collation is the database's actual one.</param>
    /// <param name="configured">What the host is configured with; a null collation means whichever the database has.</param>
    /// <returns>One sentence per difference; empty when the store can be served.</returns>
    public static IReadOnlyList<string> DifferencesFrom(this IStoreSettings stored, IStoreSettings configured)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(configured);
        var differences = new List<string>();
        if (stored.Partitioning != configured.Partitioning)
        {
            differences.Add($"Nightingale:Store:Partitioning is {configured.Partitioning} but the store was initialized with {stored.Partitioning}");
        }

        if (stored.AssignOrdinals != configured.AssignOrdinals)
        {
            differences.Add(stored.AssignOrdinals
                ? "Nightingale:Store:AssignOrdinals is false but the store was initialized with ordinals"
                : "Nightingale:Store:AssignOrdinals is true but the store was initialized without ordinals, and there is no backfill yet");
        }

        if (!string.Equals(stored.Schema, configured.Schema, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"Nightingale:Store:Schema is {configured.Schema} but the store was initialized in {stored.Schema}");
        }

        if (configured.Collation is not null && !string.Equals(configured.Collation, stored.Collation, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"Nightingale:Store:Collation is {configured.Collation} but the database's collation is {stored.Collation}");
        }

        return differences;
    }
}
