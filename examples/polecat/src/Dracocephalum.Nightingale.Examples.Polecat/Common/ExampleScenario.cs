namespace Dracocephalum.Nightingale.Examples.Polecat.Common;

/// <summary>
/// One scenario as the program knows it: the name it is asked for by, a line that says what it
/// shows, and how to run it. A scenario reports in a type of its own, which its test asserts;
/// the program only needs the closing line, so that is what running one returns here.
/// </summary>
/// <param name="Name">The name on the command line, in kebab case.</param>
/// <param name="Summary">What the scenario shows, in one line.</param>
/// <param name="Run">Runs it: master connection string, output, cancellation; returns the closing line.</param>
public sealed record ExampleScenario(string Name, string Summary, Func<string, TextWriter, CancellationToken, Task<string>> Run)
{
    /// <summary>Makes a scenario from its own run and the closing line its report gives.</summary>
    /// <typeparam name="TReport">What the scenario reports.</typeparam>
    /// <param name="name">The name on the command line, in kebab case.</param>
    /// <param name="summary">What the scenario shows, in one line.</param>
    /// <param name="run">The scenario: master connection string, output, cancellation.</param>
    /// <param name="done">The closing line, from the report.</param>
    /// <returns>The scenario.</returns>
    public static ExampleScenario Create<TReport>(string name, string summary, Func<string, TextWriter, CancellationToken, Task<TReport>> run, Func<TReport, string> done)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(done);
        return new(name, summary, async (master, output, cancellationToken) => done(await run(master, output, cancellationToken).ConfigureAwait(false)));
    }
}
