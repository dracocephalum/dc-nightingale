using Dracocephalum.Nightingale.Server.Sequencing;
using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>
/// Checkpoints' judgement over a horizon stood in: final once the horizon has passed their
/// marker and they are older than the minimum age, never before the age, and held, with who holds
/// them, while an open transaction does; every checkpoint old enough asked in one call.
/// </summary>
public sealed class HorizonCheckpointTests
{
    private static readonly HorizonMarker Marked = new(42);
    private static readonly SequenceKey Acme = new("acme");
    private static readonly SequenceKey Globex = new("globex");

    private readonly FakeTimeProvider _clock = new();
    private readonly ITransactionHorizon _horizon = A.Fake<ITransactionHorizon>();

    public HorizonCheckpointTests()
    {
        A.CallTo(() => _horizon.MarkAsync(A<CancellationToken>._)).Returns(Marked);
        A.CallTo(() => _horizon.MinimumAge).Returns(TimeSpan.Zero);
        HoldNone();
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TakeAsync_ShouldKeepEachCeilingUnderOneMarker()
    {
        // Act
        var sut = await TakeAsync();

        // Assert
        sut[Acme].Ceiling.ShouldBe(17);
        sut[Globex].Ceiling.ShouldBe(23);
        sut.Values.ShouldAllBe(checkpoint => checkpoint.Marker == Marked);
        A.CallTo(() => _horizon.MarkAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task JudgeAsync_WhenTheHorizonHasPassed_ShouldBeFinal()
    {
        // Arrange
        var checkpoints = await TakeAsync();

        // Act
        var judgements = await HorizonCheckpoint.JudgeAsync(_horizon, checkpoints, Cancellation);

        // Assert
        judgements[Acme].ShouldBe(CheckpointJudgement.Final);
        judgements[Globex].ShouldBe(CheckpointJudgement.Final);
    }

    [Fact]
    public async Task JudgeAsync_WhenATransactionHoldsOneSequence_ShouldBeHeldByItThereAlone()
    {
        // Arrange
        var holder = new HorizonHolder(55, "writer", DateTimeOffset.UnixEpoch);
        A.CallTo(() => _horizon.FindHoldersAsync(A<IReadOnlyDictionary<SequenceKey, HorizonMarker>>._, A<CancellationToken>._))
            .Returns(new Dictionary<SequenceKey, HorizonHolder> { [Acme] = holder });
        var checkpoints = await TakeAsync();

        // Act
        var judgements = await HorizonCheckpoint.JudgeAsync(_horizon, checkpoints, Cancellation);

        // Assert
        judgements[Acme].IsFinal.ShouldBeFalse();
        judgements[Acme].Holder.ShouldBe(holder);
        judgements[Globex].ShouldBe(CheckpointJudgement.Final);
    }

    [Fact]
    public async Task JudgeAsync_ShouldAskTheHorizonOnceForEverySequence()
    {
        // Arrange
        var checkpoints = await TakeAsync();

        // Act
        await HorizonCheckpoint.JudgeAsync(_horizon, checkpoints, Cancellation);

        // Assert
        A.CallTo(() => _horizon.FindHoldersAsync(
                A<IReadOnlyDictionary<SequenceKey, HorizonMarker>>.That.Matches(markers => markers.Count == 2 && markers[Acme] == Marked && markers[Globex] == Marked),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task JudgeAsync_BeforeTheMinimumAge_ShouldBeTooYoungWithoutAskingTheHorizon()
    {
        // Arrange
        A.CallTo(() => _horizon.MinimumAge).Returns(TimeSpan.FromMinutes(1));
        var checkpoints = await TakeAsync();
        _clock.Advance(TimeSpan.FromSeconds(59));

        // Act
        var judgements = await HorizonCheckpoint.JudgeAsync(_horizon, checkpoints, Cancellation);

        // Assert
        judgements.Values.ShouldAllBe(judgement => judgement == CheckpointJudgement.TooYoung);
        A.CallTo(() => _horizon.FindHoldersAsync(A<IReadOnlyDictionary<SequenceKey, HorizonMarker>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task JudgeAsync_OnceTheMinimumAgeHasPassed_ShouldAskTheHorizon()
    {
        // Arrange
        A.CallTo(() => _horizon.MinimumAge).Returns(TimeSpan.FromMinutes(1));
        var checkpoints = await TakeAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        var judgements = await HorizonCheckpoint.JudgeAsync(_horizon, checkpoints, Cancellation);

        // Assert
        judgements[Acme].ShouldBe(CheckpointJudgement.Final);
    }

    [Fact]
    public async Task GetAge_ShouldRunOnTheGivenClock()
    {
        // Arrange
        var sut = (await TakeAsync())[Acme];

        // Act
        _clock.Advance(TimeSpan.FromSeconds(3));

        // Assert
        sut.GetAge().ShouldBe(TimeSpan.FromSeconds(3));
    }

    private void HoldNone() =>
        A.CallTo(() => _horizon.FindHoldersAsync(A<IReadOnlyDictionary<SequenceKey, HorizonMarker>>._, A<CancellationToken>._))
            .Returns(new Dictionary<SequenceKey, HorizonHolder>());

    private ValueTask<IReadOnlyDictionary<SequenceKey, HorizonCheckpoint>> TakeAsync() =>
        HorizonCheckpoint.TakeAsync(_horizon, new Dictionary<SequenceKey, long> { [Acme] = 17, [Globex] = 23 }, _clock, Cancellation);
}
