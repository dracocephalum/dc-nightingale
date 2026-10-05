namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// One thing known about the store, as a name and a value: a setting it was initialized with,
/// or a fact such as who initialized it and when. One level, a value each, never an object. The
/// names of the settings are the members of the options type the configuration binds into, so
/// the rows bind into it too, and the table's shape never changes when a property is added: an
/// older store simply has no row for it and reads as the default.
/// </summary>
public sealed class StoreProperty
{
    /// <summary>Gets or sets the row's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the property's name; unique.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the value, as configuration writes it.</summary>
    public string? Value { get; set; }
}
