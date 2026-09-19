// Runs the scenario against the local SQL Server and narrates it. Exit code 0 when
// every step completed, 1 otherwise; the test project asserts the same run.
using Dracocephalum.Nightingale.Examples.Common;
using Dracocephalum.Nightingale.Examples.PersistentSubscription;

return await ExampleProgram.RunAsync(Scenario.RunAsync, report => $"Done: {report.Delivered.Count} delivered, one parked and replayed as {report.ReplayedType}; the group ended at checkpoint {report.CheckpointOnReturn}.");
