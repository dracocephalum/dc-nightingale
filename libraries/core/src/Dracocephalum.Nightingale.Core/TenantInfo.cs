namespace Dracocephalum.Nightingale;

/// <summary>A tenant as the server describes it.</summary>
/// <param name="Id">The id every call and credential names it by.</param>
/// <param name="Name">The name, for people.</param>
/// <param name="IsDisabled">Whether its calls are refused and its consumers disconnected.</param>
/// <param name="CreatedAt">When it was created.</param>
public sealed record TenantInfo(Guid Id, string Name, bool IsDisabled, DateTimeOffset CreatedAt);
