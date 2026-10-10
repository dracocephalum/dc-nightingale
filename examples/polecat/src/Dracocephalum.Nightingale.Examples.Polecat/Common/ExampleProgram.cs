namespace Dracocephalum.Nightingale.Examples.Polecat.Common;

/// <summary>
/// The program around the scenarios. Without an argument it lists them; with names it runs
/// those, in the order given; with <c>all</c> it runs every one. The scenarios asked for share one
/// run against the environment's SQL Server: one database, created and initialized by the run's
/// server, and a tenant for each scenario. Each scenario narrates its steps and ends with one
/// closing line. The exit code is 0 when every scenario asked for completed, 1 when one failed,
/// and 2 when a name is not a scenario's. Ctrl+C cancels the run and still leaves the server as
/// it was found, because the run's database is dropped on the way out.
/// </summary>
public static class ExampleProgram
{
    /// <summary>The argument that runs every scenario.</summary>
    public const string All = "all";

    /// <summary>Runs the scenarios the arguments name.</summary>
    /// <param name="args">The command line: nothing, scenario names, or <see cref="All"/>.</param>
    /// <param name="scenarios">The scenarios there are.</param>
    /// <param name="output">Where the steps are narrated; the console when null.</param>
    /// <param name="error">Where a failure is reported; the console's error stream when null.</param>
    /// <param name="run">The run to give the scenarios; when null, the program starts one for them and drops its database at the end.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IReadOnlyList<ExampleScenario> scenarios, TextWriter? output = null, TextWriter? error = null, ExampleRun? run = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(scenarios);
        output ??= Console.Out;
        error ??= Console.Error;

        if (args.Count == 0)
        {
            await output.WriteLineAsync($"Name a scenario to run, several, or '{All}':").ConfigureAwait(false);
            await ListAsync(scenarios, output).ConfigureAwait(false);
            return 0;
        }

        var chosen = new List<ExampleScenario>();
        foreach (var name in args)
        {
            if (string.Equals(name, All, StringComparison.Ordinal))
            {
                chosen.AddRange(scenarios);
            }
            else if (scenarios.FirstOrDefault(scenario => string.Equals(scenario.Name, name, StringComparison.Ordinal)) is { } scenario)
            {
                chosen.Add(scenario);
            }
            else
            {
                await error.WriteLineAsync($"'{name}' is not a scenario. The scenarios are:").ConfigureAwait(false);
                await ListAsync(scenarios, error).ConfigureAwait(false);
                return 2;
            }
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return run is null
                ? await RunOwnAsync(chosen, output, error, cancellation.Token).ConfigureAwait(false)
                : await RunAllAsync(chosen, run, output, error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Starts a run for the scenarios, says so, runs them, and drops the run's database at the end, cancelled or not.</summary>
    private static async Task<int> RunOwnAsync(IReadOnlyList<ExampleScenario> chosen, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ExampleRun run;
        try
        {
            run = await ExampleRun.StartAsync(ExampleEnvironment.ResolveMasterConnectionString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Failed to start the run: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }

        await using (run)
        {
            await output.WriteLineAsync($"Database {run.Database.Name} created and initialized by the run's server at {run.Server.Address}; a tenant per scenario.").ConfigureAwait(false);
            try
            {
                return await RunAllAsync(chosen, run, output, error, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await output.WriteLineAsync("Dropping the database.").ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> RunAllAsync(IReadOnlyList<ExampleScenario> chosen, ExampleRun run, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var failed = 0;
        foreach (var scenario in chosen)
        {
            if (chosen.Count > 1)
            {
                await output.WriteLineAsync($"== {scenario.Name} ==").ConfigureAwait(false);
            }

            try
            {
                await output.WriteLineAsync(await scenario.Run(run, output, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                await error.WriteLineAsync($"Failed: {scenario.Name}: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            }
        }

        if (chosen.Count > 1)
        {
            await output.WriteLineAsync($"{chosen.Count - failed} of {chosen.Count} scenarios completed.").ConfigureAwait(false);
        }

        return failed == 0 ? 0 : 1;
    }

    private static async Task ListAsync(IReadOnlyList<ExampleScenario> scenarios, TextWriter writer)
    {
        var width = scenarios.Max(scenario => scenario.Name.Length);
        foreach (var scenario in scenarios)
        {
            await writer.WriteLineAsync($"  {scenario.Name.PadRight(width)}  {scenario.Summary}").ConfigureAwait(false);
        }
    }
}
