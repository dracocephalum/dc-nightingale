using Dracocephalum.Nightingale.Client;

namespace Dracocephalum.Nightingale.Examples.Polecat.Common;

/// <summary>
/// A client opened from a connection string, the way an application opens one, and disposed with
/// it. The string says <c>tls=false</c>, the sample server speaking cleartext HTTP/2 on a
/// loopback port, and a cleartext connection of the client's never goes through the machine's
/// HTTP proxy, so the samples run the same where the environment names one.
/// </summary>
public sealed class ExampleConnection : IAsyncDisposable
{
    internal ExampleConnection(string connectionString)
    {
        ConnectionString = connectionString;
        Client = new NightingaleClient(connectionString);
    }

    /// <summary>Gets the connection string the client was opened from.</summary>
    public string ConnectionString { get; }

    /// <summary>Gets the client.</summary>
    public NightingaleClient Client { get; }

    /// <summary>Connects one client to several servers of one cluster, named together in one connection string, working in the given tenant.</summary>
    /// <param name="tenant">The tenant every call names.</param>
    /// <param name="servers">The servers.</param>
    /// <returns>The connection; dispose it to close its channels.</returns>
    public static ExampleConnection ToCluster(TenantInfo tenant, params ExampleServer[] servers)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(servers);
        return new(ToConnectionString(UserCredentials.AdminUserName, ExampleServer.AdminPassword, string.Join(',', servers.Select(server => server.Address.Authority)), tenant.Id));
    }

    /// <summary>Builds the connection string the samples open their clients from.</summary>
    /// <param name="name">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="authority">The server, or the servers of one cluster separated by commas, as host and port.</param>
    /// <param name="tenant">The tenant to name on every call, or none.</param>
    /// <returns>The connection string.</returns>
    internal static string ToConnectionString(string name, string password, string authority, Guid? tenant) =>
        $"nightingale://{Uri.EscapeDataString(name)}:{Uri.EscapeDataString(password)}@{authority}?tls=false{(tenant is { } id ? "&tenant=" + id.ToString("D") : string.Empty)}";

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
