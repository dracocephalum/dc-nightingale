namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// One setting the store was initialized with, as a name and a value. The names are
/// configuration paths, so the rows bind into the same options type the configuration binds
/// into, and the table's shape never changes when a setting is added: an older store simply has
/// no row for it and reads as the default.
/// </summary>
public sealed class Setting
{
    /// <summary>Gets or sets the row's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the setting's name, a configuration path; unique.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the value, as configuration writes it.</summary>
    public string? Value { get; set; }
}
