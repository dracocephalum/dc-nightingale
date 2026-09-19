// Runs the scenario against the local SQL Server and narrates it. Exit code 0 when
// every step completed, 1 otherwise; the test project asserts the same run.
using Dracocephalum.Nightingale.Examples.AppendAndRead;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

try
{
    var report = await Scenario.RunAsync(Scenario.ResolveMasterConnectionString(), Console.Out, cancellation.Token);
    Console.WriteLine($"Done: {report.ReadForwards.Count} events round-tripped on {report.Stream}.");
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"Failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
