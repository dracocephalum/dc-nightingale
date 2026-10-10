using Dracocephalum.Nightingale.Examples.Polecat.Common;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The collection for the scenario tests: one run for all of them, started before the first and
/// disposed after the last, so the test project creates one database and drops it, as a run of
/// the program does. A collection runs its tests one at a time, as the program runs the scenarios.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SharedRun : ICollectionFixture<SharedRun.Fixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "shared-run";

    /// <summary>The run the collection shares, against the local SQL Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default.</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private ExampleRun? _run;

        /// <summary>Gets the run.</summary>
        public ExampleRun Run => _run ?? throw new InvalidOperationException("The run has not started.");

        /// <inheritdoc/>
        public async ValueTask InitializeAsync() =>
            _run = await ExampleRun.StartAsync(ExampleEnvironment.ResolveMasterConnectionString(), TestContext.Current.CancellationToken);

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_run is { } run)
            {
                await run.DisposeAsync();
            }
        }
    }
}
