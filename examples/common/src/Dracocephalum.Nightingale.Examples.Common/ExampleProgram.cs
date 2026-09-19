namespace Dracocephalum.Nightingale.Examples.Common;

/// <summary>
/// The shell every sample program is: run the scenario against the environment's SQL Server,
/// narrate it, print one closing line, and exit with 0 when every step completed and 1
/// otherwise. Ctrl+C cancels the run and still leaves the server as it was found, because the
/// scenario's database is dropped on the way out.
/// </summary>
public static class ExampleProgram
{
    /// <summary>Runs a scenario as a program.</summary>
    /// <typeparam name="TReport">What the scenario reports.</typeparam>
    /// <param name="scenario">The scenario: master connection string, output, cancellation.</param>
    /// <param name="done">The closing line, from the report.</param>
    /// <param name="output">Where the steps are narrated; the console when null.</param>
    /// <param name="error">Where a failure is reported; the console's error stream when null.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync<TReport>(Func<string, TextWriter, CancellationToken, Task<TReport>> scenario, Func<TReport, string> done, TextWriter? output = null, TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(done);
        output ??= Console.Out;
        error ??= Console.Error;

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var report = await scenario(ExampleEnvironment.ResolveMasterConnectionString(), output, cancellation.Token).ConfigureAwait(false);
            await output.WriteLineAsync(done(report)).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Failed: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
