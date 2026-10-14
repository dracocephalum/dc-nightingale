namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// Marks an enum whose members' numbers are what a column stores, where the repository's rule
/// stores an enum by name. Only for a column whose width counts: one repeated in a key and an
/// index of a table that grows with the events. The numbers are then part of the schema: every
/// member takes an explicit value, a value is never changed or reused, and a test pins them.
/// <see cref="ModelConventions"/> accepts a numeric enum column only when its enum carries this.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, Inherited = false)]
public sealed class StoredAsNumberAttribute : Attribute;
