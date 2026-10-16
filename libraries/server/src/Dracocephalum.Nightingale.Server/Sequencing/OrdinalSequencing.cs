using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The ordinal sequencer as a backend registers it: the server's loop, lease and follower over the
/// backend's <see cref="IOrdinalSequencer"/>, numbering the one shared sequence or a sequence per
/// tenant. A backend registers its tails first, so the sequencer's first look at a head is a real
/// one.
/// </summary>
public static class OrdinalSequencing
{
    /// <summary>The lease every instance competes for.</summary>
    public const string LeaseName = "ordinals";

    /// <summary>How long a lease lasts without renewal, after which another instance takes over, unless the host was given another.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>Registers the sequencer's host, sequencing each batch through the backend's sequencer.</summary>
    /// <param name="services">The service collection, holding the tails, the group store and the gateway's context.</param>
    /// <param name="sequencer">Makes the backend's sequencer.</param>
    /// <param name="sequencePerTenant">Whether the store has a sequence per tenant rather than one shared sequence.</param>
    /// <param name="followInterval">How long the follower waits between reads of the progress while a sequence is behind.</param>
    /// <param name="enabled">Whether the operator lets the sequencer run; disabled, nothing is numbered, and every read and subscription by ordinal is refused with the reason, until the server starts enabled again.</param>
    /// <param name="leaseDuration">How long the lease lasts without renewal; <see cref="LeaseDuration"/> when not given.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleOrdinalSequencer(this IServiceCollection services, Func<IServiceProvider, IOrdinalSequencer> sequencer, bool sequencePerTenant, TimeSpan followInterval, bool enabled, TimeSpan? leaseDuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sequencer);
        if (!enabled)
        {
            // The numbering of every sequence stopped with the operator's reason, before any call
            // is served; what is numbered stays readable from the progress, untouched.
            services.AddSingleton<IHostedService>(provider => new DisabledOrdinalSequencer(provider.GetRequiredService<SequenceTails>()));
            return services;
        }

        // The registry is the server's, but a host that registers only the backend still numbers.
        services.TryAddSingleton<SubscriptionGroupRegistry>();
        services.AddSingleton<INumberedSequences>(provider => sequencePerTenant
            ? new TenantSequences(provider.GetRequiredService<SequenceTails>(), provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>())
            : new SharedSequence(provider.GetRequiredService<IStoreTail>(), provider.GetRequiredService<SequenceTails>().NumberingOf(SequenceKey.Shared)));
        services.AddSingleton<IHostedService>(provider => new OrdinalSequencerHost(
            provider.GetRequiredService<ISubscriptionGroupStore>(),
            provider.GetRequiredService<INumberedSequences>(),
            new OrdinalSequencingFollower(
                provider.GetRequiredService<INumberedSequences>(),
                new OrdinalSequencingProgressReader(provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>()),
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                provider.GetService<ILogger<OrdinalSequencingFollower>>() ?? NullLogger<OrdinalSequencingFollower>.Instance,
                followInterval),
            provider.GetRequiredService<SubscriptionGroupRegistry>(),
            sequencer(provider),
            new OrdinalSequencingFaults(provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>()),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<ILogger<OrdinalSequencerHost>>() ?? NullLogger<OrdinalSequencerHost>.Instance,
            leaseDuration));
        return services;
    }
}
