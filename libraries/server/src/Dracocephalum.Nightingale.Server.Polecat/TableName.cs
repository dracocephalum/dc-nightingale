namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>A table's name in the database's catalog, with the schema it is in.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="Name">The table's name inside the schema.</param>
internal sealed record TableName(string Schema, string Name)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Schema}.{Name}";
}
