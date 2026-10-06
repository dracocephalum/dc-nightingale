namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// A group running in this instance, under its lease, for as long as it has consumers here: the
/// runtime that delivers, and the keeper that renews the lease, redelivers what timed out and
/// writes how the group stands. The keeper outlives any one consumer's call; when it fails, the
/// lease lost to another instance, every consumer's call ends with the cause.
/// </summary>
internal sealed class SubscriptionGroupHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Initializes a new instance of the <see cref="SubscriptionGroupHost"/> class and starts the keeper.</summary>
    /// <param name="definition">The group as read from the store.</param>
    /// <param name="runtime">The running group.</param>
    /// <param name="keep">The keeper's loop, which runs until stopped and throws when the group can no longer be kept.</param>
    public SubscriptionGroupHost(SubscriptionGroupDefinition definition, SubscriptionGroupRuntime runtime, Func<SubscriptionGroupRuntime, CancellationToken, Task> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        Definition = definition;
        Runtime = runtime;
        Keeping = Task.Run(() => KeepAsync(keep));
    }

    /// <summary>Gets the group as read from the store when it was started.</summary>
    public SubscriptionGroupDefinition Definition { get; }

    /// <summary>Gets the running group.</summary>
    public SubscriptionGroupRuntime Runtime { get; }

    /// <summary>Gets the keeper: faulted, with the cause, when the group can no longer be kept here.</summary>
    public Task Keeping { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Keeping.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping is the point; a keeper's failure already ended every consumer's call.
        }

        await Runtime.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task KeepAsync(Func<SubscriptionGroupRuntime, CancellationToken, Task> keep)
    {
        try
        {
            await keep(Runtime, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception cause)
        {
            Runtime.Fail(cause);
            throw;
        }
    }
}
