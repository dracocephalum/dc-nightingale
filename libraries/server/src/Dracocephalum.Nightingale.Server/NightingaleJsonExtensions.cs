using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Gives a host's own JSON the settings Nightingale serializes with. The gRPC services need none
/// of this; it is for a host that also serves JSON, minimal endpoints or controllers, and wants
/// one answer to how its JSON looks. The settings are the ones in <see cref="NightingaleJson"/>,
/// applied by the same call, so the host's options and the library's cannot drift apart.
/// </summary>
public static class NightingaleJsonExtensions
{
    /// <summary>Applies Nightingale's JSON settings to the host's minimal-endpoint and controller JSON options.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="environment">
    /// The host's environment, when the host wants its JSON readable while it is being worked on:
    /// given, and anything but production, the host's JSON is written indented. Not given, or
    /// production, it is written compact. Only the host's own JSON is affected, never what the
    /// gateway passes through.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleJson(this IServiceCollection services, IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var indented = environment is not null && !environment.IsProduction();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options => NightingaleJson.Configure(options.SerializerOptions, indented));
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => NightingaleJson.Configure(options.JsonSerializerOptions, indented));
        return services;
    }
}
