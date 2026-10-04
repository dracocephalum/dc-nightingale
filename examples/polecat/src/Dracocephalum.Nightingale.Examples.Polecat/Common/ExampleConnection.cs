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

    /// <summary>Connects one client to several servers of one cluster, named together in one connection string.</summary>
    /// <param name="servers">The servers.</param>
    /// <returns>The connection; dispose it to close its channels.</returns>
    public static ExampleConnection ToCluster(params ExampleServer[] servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return new($"nightingale://{string.Join(',', servers.Select(server => server.Address.Authority))}?tls=false");
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
