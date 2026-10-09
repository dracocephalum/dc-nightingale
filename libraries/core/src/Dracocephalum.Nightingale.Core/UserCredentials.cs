namespace Dracocephalum.Nightingale;

/// <summary>A user name and password, sent with every call in the standard <c>authorization</c> header under the Basic scheme.</summary>
/// <param name="UserName">The user name.</param>
/// <param name="Password">The password.</param>
public sealed record UserCredentials(string UserName, string Password)
{
    /// <summary>The name of the built-in administrator, whose password the server takes from its configuration.</summary>
    public const string AdminUserName = "admin";
}
