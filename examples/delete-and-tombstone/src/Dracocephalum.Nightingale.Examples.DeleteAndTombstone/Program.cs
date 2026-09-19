// Runs the scenario against the local SQL Server and narrates it. Exit code 0 when
// every step completed, 1 otherwise; the test project asserts the same run.
using Dracocephalum.Nightingale.Examples.DeleteAndTombstone;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

try
{
    var report = await Scenario.RunAsync(Scenario.ResolveMasterConnectionString(), Console.Out, cancellation.Token);
    Console.WriteLine($"Done: deleted, tombstoned and revived {report.Stream}; the default server refused with {report.DefaultServerRefusal}.");
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"Failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
