using Dracocephalum.Nightingale.Server.Grpc;
using Google.Rpc;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Grpc;

/// <summary>The translation of the sequencer's states into the statuses and reasons the contract promises.</summary>
public sealed class NightingaleErrorsTests
{
    [Fact]
    public void TryTranslate_WhenTheSequencerIsDisabledOrFaulted_ShouldGiveTheReasonAndStatusEachPromises()
    {
        // Act
        var disabled = NightingaleErrors.TryTranslate(new OrdinalSequencerDisabledException(), out var whenDisabled);
        var faulted = NightingaleErrors.TryTranslate(new OrdinalSequencerFaultedException("the batch timed out"), out var whenFaulted);

        // Assert
        disabled.ShouldBeTrue();
        whenDisabled!.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        whenDisabled.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("SEQUENCER_DISABLED");
        faulted.ShouldBeTrue();
        whenFaulted!.StatusCode.ShouldBe(StatusCode.Unavailable);
        whenFaulted.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Metadata["cause"].ShouldBe("the batch timed out");
    }
}
