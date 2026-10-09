namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>Authentication is on by default and wants the administrator's password; a test of anything else turns it off.</summary>
internal static class TestAuth
{
    /// <summary>Turns authentication off, then applies the test's own configuration.</summary>
    /// <param name="configure">The test's own configuration, or none.</param>
    /// <returns>The configuration to register with.</returns>
    public static Action<NightingaleOptions> Off(Action<NightingaleOptions>? configure = null) => options =>
    {
        options.Auth.Enabled = false;
        configure?.Invoke(options);
    };
}
