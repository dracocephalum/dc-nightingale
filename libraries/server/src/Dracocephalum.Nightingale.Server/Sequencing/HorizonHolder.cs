namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The open transaction a horizon waits on, as far as the database says who it is: enough for an
/// operator to find the session holding the horizon back.
/// </summary>
/// <param name="SessionId">The database's id of the session, when it says.</param>
/// <param name="LoginName">The login the session runs under, when it says.</param>
/// <param name="StartedAt">When the transaction began, in UTC, when the database says in a form that can be read.</param>
public sealed record HorizonHolder(int? SessionId, string? LoginName, DateTimeOffset? StartedAt);
