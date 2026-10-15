namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>Whether a journal's horizon can be read, and what is missing when it cannot.</summary>
/// <param name="IsAvailable">Whether the horizon's query is answered.</param>
/// <param name="Reason">What is missing and how to supply it, when it is not; <see langword="null"/> when it is.</param>
public sealed record HorizonAvailability(bool IsAvailable, string? Reason)
{
    /// <summary>Gets the answer of a horizon that can be read.</summary>
    public static HorizonAvailability Available { get; } = new(true, null);

    /// <summary>The answer of a horizon that cannot be read.</summary>
    /// <param name="reason">What is missing and how to supply it.</param>
    /// <returns>The answer.</returns>
    public static HorizonAvailability Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new HorizonAvailability(false, reason);
    }
}
