namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>A table the database holds, and whether anything is in it.</summary>
/// <param name="Name">The table's name.</param>
/// <param name="HasRows">Whether the table holds at least one row.</param>
internal sealed record DatabaseTable(TableName Name, bool HasRows);
