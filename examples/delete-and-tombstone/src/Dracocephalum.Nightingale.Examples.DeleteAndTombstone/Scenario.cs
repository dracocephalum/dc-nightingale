using System.Text;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Common;

namespace Dracocephalum.Nightingale.Examples.DeleteAndTombstone;

/// <summary>
/// Deletion against a Nightingale server, step by step: a delete that hides a stream and its
/// events from every read and refuses further appends, a tombstone that removes it for good and
/// frees the name, a stale expected revision refused, and a server with the default settings
/// refusing to delete at all. The program prints the steps; the test project asserts the report.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Stream">The stream that was deleted, tombstoned and revived.</param>
    /// <param name="CategoryBefore">How many events the category stream held before the delete.</param>
    /// <param name="ReadAfterDelete">The exception a read of the deleted stream threw.</param>
    /// <param name="CategoryAfterDelete">How many events the category stream held after the delete.</param>
    /// <param name="AppendAfterDelete">The exception an append to the deleted stream threw.</param>
    /// <param name="StateAfterTombstone">What a read reported after the tombstone.</param>
    /// <param name="RevivedRevision">The revision the first append to the freed name reported.</param>
    /// <param name="ConflictActualRevision">The actual revision reported when a stale delete was refused.</param>
    /// <param name="DefaultServerRefusal">The exception a server with the default settings threw on delete.</param>
    public sealed record Report(
        string Stream,
        int CategoryBefore,
        string ReadAfterDelete,
        int CategoryAfterDelete,
        string AppendAfterDelete,
        ReadState StateAfterTombstone,
        long RevivedRevision,
        long ConflictActualRevision,
        string DefaultServerRefusal);

    /// <summary>Runs the scenario.</summary>
    /// <param name="masterConnectionString">A connection string to the server's master database.</param>
    /// <param name="output">Where the steps are narrated.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public static async Task<Report> RunAsync(string masterConnectionString, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        await using var database = ExampleDatabase.Reserve(masterConnectionString);
        await output.WriteLineAsync($"1. Reserved the database name {database.Name}; the server creates it.").ConfigureAwait(false);

        await using var server = await ExampleServer.StartAsync(database.ConnectionString, cancellationToken, options =>
        {
            options.Deletion.AllowDelete = true;
            options.Deletion.AllowTombstone = true;
        }).ConfigureAwait(false);
        await output.WriteLineAsync($"2. Server listening at {server.Address} with delete and tombstone allowed, which the default host does not.").ConfigureAwait(false);

        await using var connection = server.Connect();
        var client = connection.Client;
        const string stream = "orders-1";

        await client.AppendToStreamAsync(stream, StreamState.NoStream, [Event("order_placed"), Event("order_paid")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("orders-2", StreamState.NoStream, [Event("order_placed")], cancellationToken).ConfigureAwait(false);
        var before = await CountAsync(client, "$ce-orders", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"3. Appended two events to {stream} and one to orders-2; $ce-orders holds {before}.").ConfigureAwait(false);

        long conflictActual;
        try
        {
            await client.DeleteStreamAsync(stream, StreamState.StreamRevision(0), cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A stale delete was accepted.");
        }
        catch (RevisionConflictException conflict)
        {
            conflictActual = conflict.ActualRevision;
            await output.WriteLineAsync($"4. A delete expecting revision 0 was refused; the stream is at revision {conflictActual}.").ConfigureAwait(false);
        }

        await client.DeleteStreamAsync(stream, StreamState.StreamRevision(1), cancellationToken).ConfigureAwait(false);
        string readAfterDelete;
        await using (var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken))
        {
            readAfterDelete = await Failure(async () => await read.ReadState.ConfigureAwait(false)).ConfigureAwait(false);
        }

        var after = await CountAsync(client, "$ce-orders", cancellationToken).ConfigureAwait(false);
        var appendAfterDelete = await Failure(() => client.AppendToStreamAsync(stream, StreamState.Any, [Event("order_shipped")], cancellationToken)).ConfigureAwait(false);
        await output.WriteLineAsync($"5. Deleted {stream}: a read fails with {readAfterDelete}, $ce-orders holds {after}, an append fails with {appendAfterDelete}.").ConfigureAwait(false);

        await client.TombstoneStreamAsync(stream, StreamState.Any, cancellationToken).ConfigureAwait(false);
        ReadState afterTombstone;
        await using (var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken))
        {
            afterTombstone = await read.ReadState.ConfigureAwait(false);
        }

        await output.WriteLineAsync($"6. Tombstoned {stream}: a read now reports {afterTombstone}, as for a stream that never existed.").ConfigureAwait(false);

        var revived = await client.AppendToStreamAsync(stream, StreamState.NoStream, [Event("order_placed")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"7. Appended to {stream} again as a new stream; revision {revived.Revision}.").ConfigureAwait(false);

        await using var defaultServer = await ExampleServer.StartAsync(database.ConnectionString, cancellationToken).ConfigureAwait(false);
        await using var defaultConnection = defaultServer.Connect();
        var defaultClient = defaultConnection.Client;
        var refusal = await Failure(() => defaultClient.DeleteStreamAsync(stream, StreamState.Any, cancellationToken)).ConfigureAwait(false);
        await output.WriteLineAsync($"8. A second server with the default settings refused to delete {stream}: {refusal}.").ConfigureAwait(false);
        await output.WriteLineAsync("9. Dropping the database.").ConfigureAwait(false);

        return new Report(stream, before, readAfterDelete, after, appendAfterDelete, afterTombstone, revived.Revision, conflictActual, refusal);
    }

    private static async Task<int> CountAsync(NightingaleClient client, string stream, CancellationToken cancellationToken)
    {
        var count = 0;
        await using var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken);
        await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            count += record.Revision >= 0 ? 1 : 0;
        }

        return count;
    }

    /// <summary>Runs a call that is expected to fail and names the exception it failed with.</summary>
    private static async Task<string> Failure(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception.GetType().Name;
        }

        throw new InvalidOperationException("The call was expected to fail and did not.");
    }

    private static EventData Event(string type) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));
}
