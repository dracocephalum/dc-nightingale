namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// Marks a table of the gateway's that extends one of the event store's: data that could be
/// columns of the store's rows, kept in a table of its own only so that the rows without it cost
/// nothing. Such a table is keyed as data of the store's is, by what it describes, not by an id
/// of its own: an id no code gives would be one more thing the database invents for every row,
/// on a table that grows with the events. <see cref="ModelConventions"/> exempts it from the rule
/// of one <c>Guid</c> key named <c>Id</c>, and from that rule only.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StoreExtensionAttribute : Attribute;
