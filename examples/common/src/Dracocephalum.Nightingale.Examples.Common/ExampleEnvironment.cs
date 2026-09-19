namespace Dracocephalum.Nightingale.Examples.Common;

/// <summary>
/// The one assumption every sample makes: a SQL Server reachable with a connection string to its
/// <c>master</c> database, with permission to create and drop databases.
/// </summary>
public static class ExampleEnvironment
{
    /// <summary>The environment variable that names the master connection string.</summary>
    public const string MasterConnectionStringVariable = "NIGHTINGALE_SQLSERVER";

    /// <summary>The connection string used when the environment does not name one: a local server, trusted login.</summary>
    public const string DefaultMasterConnectionString = "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300";

    /// <summary>The master connection string: the environment's, or the local default.</summary>
    /// <returns>The connection string.</returns>
    public static string ResolveMasterConnectionString() =>
        Environment.GetEnvironmentVariable(MasterConnectionStringVariable) ?? DefaultMasterConnectionString;
}
