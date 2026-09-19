using System.Net;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Server;
using Dracocephalum.Nightingale.Server.Polecat;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Examples.Common;

/// <summary>
/// A Nightingale server hosted in the sample's own process, on a loopback port the operating
/// system picks, speaking cleartext HTTP/2 so no certificate is needed. It is wired exactly as the
/// default host is: the server extensions plus the store registration, nothing else, and a sample
/// that needs a setting the default host does not have adjusts the options when it starts one.
/// </summary>
public sealed class ExampleServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ExampleServer(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>Gets the address a client connects to.</summary>
    public Uri Address { get; }

    /// <summary>Starts a server backed by the given database.</summary>
    /// <param name="connectionString">The database's connection string.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="configure">Adjusts the options; the defaults are the default host's.</param>
    /// <returns>The running server; dispose it to stop it.</returns>
    public static async Task<ExampleServer> StartAsync(string connectionString, CancellationToken cancellationToken, Action<NightingaleOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddNightingaleServer();
        builder.Services.AddNightingalePolecat(connectionString, configure);

        var app = builder.Build();
        app.MapNightingaleServer();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("The server started without reporting an address.");
        return new ExampleServer(app, new Uri(address));
    }

    /// <summary>
    /// Connects a client to this server. The server is in this process on a loopback port, so the
    /// machine's HTTP proxy, if the environment names one, must not sit in the middle: a proxy
    /// cannot carry cleartext HTTP/2.
    /// </summary>
    /// <returns>The connection; dispose it to close the channel.</returns>
    public ExampleConnection Connect()
    {
        var channel = GrpcChannel.ForAddress(Address, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { UseProxy = false } });
        return new ExampleConnection(channel, new NightingaleClient(channel.CreateCallInvoker()));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
