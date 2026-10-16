using Microsoft.Extensions.Hosting;

namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// What runs in place of the sequencer's host when the operator disables it: nothing is numbered,
/// and the numbering of every sequence, followed now or later, is stopped with the reason before
/// any call is served, so a read or subscription by ordinal is refused with it rather than left
/// waiting.
/// </summary>
/// <param name="tails">The tails, whose numbering is stopped.</param>
internal sealed class DisabledOrdinalSequencer(SequenceTails tails) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        tails.StopNumbering(new OrdinalSequencerDisabledException());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
