using Microsoft.Extensions.DependencyInjection;

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
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleJson(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options => NightingaleJson.Configure(options.SerializerOptions));
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => NightingaleJson.Configure(options.JsonSerializerOptions));
        return services;
    }
}
