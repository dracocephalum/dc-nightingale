using System.Net;

using Dracocephalum.Nightingale.Server;
using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Polecat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Examples.Polecat.Common;

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

    /// <summary>The built-in administrator's password every sample server is started with, and every sample client connects with.</summary>
    public const string AdminPassword = "example-admin-password";

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
        builder.Services.AddNightingalePolecat(connectionString, options =>
        {
            // The samples run authenticated, as the built-in administrator, over the loopback
            // cleartext port the server listens on; a real host runs TLS and leaves the second
            // setting off. The password is the sample's, not a secret.
            options.Auth.AdminPassword = AdminPassword;
            options.Auth.AllowInsecureTransport = true;
            configure?.Invoke(options);
        });

        var app = builder.Build();
        app.MapNightingaleServer();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("The server started without reporting an address.");
        return new ExampleServer(app, new Uri(address));
    }

    /// <summary>Gets the connection string a client of this server is opened from.</summary>
    public string ConnectionString => $"nightingale://{UserCredentials.AdminUserName}:{AdminPassword}@{Address.Authority}?tls=false&tenant={Tenant.DefaultId:D}";

    /// <summary>Connects a client to this server, from its connection string.</summary>
    /// <returns>The connection; dispose it to close the channel.</returns>
    public ExampleConnection Connect() => new(ConnectionString);

    /// <summary>Opens a connection as a credential made during a run: a tenant-bound one sends no tenant, unless the scenario names one on purpose.</summary>
    /// <param name="name">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="tenant">The tenant to name on every call, or none.</param>
    /// <returns>The connection; dispose it when done.</returns>
    public ExampleConnection ConnectAs(string name, string password, Guid? tenant = null) =>
        new($"nightingale://{Uri.EscapeDataString(name)}:{Uri.EscapeDataString(password)}@{Address.Authority}?tls=false{(tenant is { } id ? "&tenant=" + id.ToString("D") : string.Empty)}");

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
