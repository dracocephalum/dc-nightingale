using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// Reads the tenants again on the configured cadence, which is how a tenant disabled or enabled
/// on another instance is noticed here. The loop reports its own end: a tick that fails is
/// logged and the next comes, and the loop ends only with the host.
/// </summary>
public sealed partial class TenantRefresher(TenantDirectory directory, NightingaleOptionsBase options, TimeProvider time, ILogger<TenantRefresher> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Tenants.RefreshInterval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await directory.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
        catch (Exception exception)
        {
            LogStopped(logger, exception);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The tenant refresh loop stopped; tenants disabled elsewhere are no longer noticed on this instance.")]
    private static partial void LogStopped(ILogger logger, Exception exception);
}
