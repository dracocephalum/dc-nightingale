using Dracocephalum.Nightingale.Server.Sequencing;
using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>
/// A checkpoint's judgement over a horizon stood in: final once the horizon has passed its
/// marker and it is older than the minimum age, never before the age, and held, with who holds
/// it, while an open transaction does.
/// </summary>
public sealed class HorizonCheckpointTests
{
    private static readonly HorizonMarker Marked = new(42);

    private readonly FakeTimeProvider _clock = new();
    private readonly ITransactionHorizon _horizon = A.Fake<ITransactionHorizon>();

    public HorizonCheckpointTests()
    {
        A.CallTo(() => _horizon.MarkAsync(A<CancellationToken>._)).Returns(Marked);
        A.CallTo(() => _horizon.MinimumAge).Returns(TimeSpan.Zero);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TakeAsync_ShouldKeepTheCeilingAndTheHorizonsMarker()
    {
        // Act
        var sut = await HorizonCheckpoint.TakeAsync(_horizon, 17, _clock, Cancellation);

        // Assert
        sut.Ceiling.ShouldBe(17);
        sut.Marker.ShouldBe(Marked);
    }

    [Fact]
    public async Task JudgeAsync_WhenTheHorizonHasPassed_ShouldBeFinal()
    {
        // Arrange
        A.CallTo(() => _horizon.FindHolderAsync(Marked, A<CancellationToken>._)).Returns((HorizonHolder?)null);
        var sut = await HorizonCheckpoint.TakeAsync(_horizon, 17, _clock, Cancellation);

        // Act
        var judgement = await sut.JudgeAsync(Cancellation);

        // Assert
        judgement.ShouldBe(CheckpointJudgement.Final);
    }

    [Fact]
    public async Task JudgeAsync_WhenATransactionHoldsTheHorizon_ShouldBeHeldByIt()
    {
        // Arrange
        var holder = new HorizonHolder(55, "writer", DateTimeOffset.UnixEpoch);
        A.CallTo(() => _horizon.FindHolderAsync(Marked, A<CancellationToken>._)).Returns(holder);
        var sut = await HorizonCheckpoint.TakeAsync(_horizon, 17, _clock, Cancellation);

        // Act
        var judgement = await sut.JudgeAsync(Cancellation);

        // Assert
        judgement.IsFinal.ShouldBeFalse();
        judgement.Holder.ShouldBe(holder);
    }

    [Fact]
    public async Task JudgeAsync_BeforeTheMinimumAge_ShouldBeTooYoungWithoutAskingTheHorizon()
    {
        // Arrange
        A.CallTo(() => _horizon.MinimumAge).Returns(TimeSpan.FromMinutes(1));
        var sut = await HorizonCheckpoint.TakeAsync(_horizon, 17, _clock, Cancellation);
        _clock.Advance(TimeSpan.FromSeconds(59));

        // Act
        var judgement = await sut.JudgeAsync(Cancellation);

        // Assert
        judgement.ShouldBe(CheckpointJudgement.TooYoung);
        A.CallTo(() => _horizon.FindHolderAsync(A<HorizonMarker>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task JudgeAsync_OnceTheMinimumAgeHasPassed_ShouldAskTheHorizon()
    {
        // Arrange
        A.CallTo(() => _horizon.MinimumAge).Returns(TimeSpan.FromMinutes(1));
        A.CallTo(() => _horizon.FindHolderAsync(Marked, A<CancellationToken>._)).Returns((HorizonHolder?)null);
        var sut = await HorizonCheckpoint.TakeAsync(_horizon, 17, _clock, Cancellation);
        _clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        var judgement = await sut.JudgeAsync(Cancellation);

        // Assert
        judgement.ShouldBe(CheckpointJudgement.Final);
    }

    [Fact]
    public async Task GetAge_ShouldRunOnTheGivenClock()
    {
        // Arrange
        var sut = await HorizonCheckpoint.TakeAsync(_horizon, 17, _clock, Cancellation);

        // Act
        _clock.Advance(TimeSpan.FromSeconds(3));

        // Assert
        sut.GetAge().ShouldBe(TimeSpan.FromSeconds(3));
    }
}
