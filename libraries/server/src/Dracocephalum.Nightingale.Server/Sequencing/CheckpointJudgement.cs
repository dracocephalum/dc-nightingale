namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>Whether every position up to a <see cref="HorizonCheckpoint"/>'s ceiling is final, and what holds it back when it is not.</summary>
/// <param name="IsFinal">Whether every position up to the ceiling is final.</param>
/// <param name="Holder">The open transaction the horizon waits on; <see langword="null"/> when final, or when the checkpoint is younger than the minimum age and the horizon was not asked.</param>
public sealed record CheckpointJudgement(bool IsFinal, HorizonHolder? Holder)
{
    /// <summary>Gets the judgement of a checkpoint the horizon has passed.</summary>
    public static CheckpointJudgement Final { get; } = new(true, null);

    /// <summary>Gets the judgement of a checkpoint younger than the horizon's minimum age.</summary>
    public static CheckpointJudgement TooYoung { get; } = new(false, null);

    /// <summary>The judgement of a checkpoint an open transaction holds back.</summary>
    /// <param name="holder">The transaction.</param>
    /// <returns>The judgement.</returns>
    public static CheckpointJudgement HeldBy(HorizonHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return new CheckpointJudgement(false, holder);
    }
}
