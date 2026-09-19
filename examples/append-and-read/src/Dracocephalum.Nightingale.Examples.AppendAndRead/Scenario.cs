using System.Text;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Common;

namespace Dracocephalum.Nightingale.Examples.AppendAndRead;

/// <summary>
/// The whole round trip a client makes against a Nightingale server, step by step, against a
/// database created for the run and dropped afterwards. The program prints the steps; the test
/// project asserts the report. Both run the same code, so the sample can never drift from what
/// is verified.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Stream">The stream the run used.</param>
    /// <param name="AppendedRevision">The revision the first append reported.</param>
    /// <param name="Head">The head the first read reported.</param>
    /// <param name="ReadForwards">Event types read forwards from the start.</param>
    /// <param name="ReadBackwards">Event types read backwards from the end, at most two.</param>
    /// <param name="CorrelationId">The correlation id read back from the first event's metadata.</param>
    /// <param name="ConflictActualRevision">The actual revision reported when a stale append was refused.</param>
    /// <param name="RetriedRevision">The revision a retried append reported.</param>
    /// <param name="MissingStreamState">What a read of a stream that never existed reported.</param>
    public sealed record Report(
        string Stream,
        long AppendedRevision,
        StreamHead Head,
        IReadOnlyList<string> ReadForwards,
        IReadOnlyList<string> ReadBackwards,
        string? CorrelationId,
        long ConflictActualRevision,
        long RetriedRevision,
        ReadState MissingStreamState);

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

        await using var server = await ExampleServer.StartAsync(database.ConnectionString, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"2. Server listening at {server.Address}; database created, store initialized.").ConfigureAwait(false);

        await using var connection = server.Connect();
        var client = connection.Client;
        var stream = "orders-" + Guid.NewGuid().ToString("N")[..8];

        var placed = Event("order_placed", "{\"orderId\":1,\"total\":42.5}", new JsonObject { ["$correlationId"] = "checkout-1" });
        var paid = Event("order_paid", "{\"orderId\":1,\"amount\":42.5}");
        var shipped = Event("order_shipped", "{\"orderId\":1,\"carrier\":\"sample\"}");
        var appended = await client.AppendToStreamAsync(stream, StreamState.NoStream, [placed, paid, shipped], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"3. Appended three events to {stream}; last revision {appended.Revision}, position {appended.Position}.").ConfigureAwait(false);

        var forwards = new List<string>();
        string? correlation = null;
        StreamHead? head;
        await using (var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken))
        {
            head = await read.Head.ConfigureAwait(false);
            await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                forwards.Add(record.Type);
                correlation ??= record.Metadata["$correlationId"]?.GetValue<string>();
            }
        }

        await output.WriteLineAsync($"4. Read forwards: {string.Join(", ", forwards)}; head {head?.First}..{head?.Last}; correlation id {correlation}.").ConfigureAwait(false);

        var backwards = new List<string>();
        await using (var read = client.ReadStreamAsync(Direction.Backwards, stream, StreamPosition.End, maxCount: 2, cancellationToken: cancellationToken))
        {
            await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                backwards.Add(record.Type);
            }
        }

        await output.WriteLineAsync($"5. Read the last two backwards: {string.Join(", ", backwards)}.").ConfigureAwait(false);

        long conflictActual;
        try
        {
            await client.AppendToStreamAsync(stream, StreamState.StreamRevision(0), [Event("order_cancelled", "{}")], cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A stale append was accepted.");
        }
        catch (RevisionConflictException conflict)
        {
            conflictActual = conflict.ActualRevision;
            await output.WriteLineAsync($"6. A stale append (expected revision 0) was refused; the stream is at revision {conflictActual}.").ConfigureAwait(false);
        }

        var retried = await client.AppendToStreamAsync(stream, StreamState.NoStream, [placed, paid, shipped], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"7. Retrying the first append with the same ids succeeded again with revision {retried.Revision}; nothing was written twice.").ConfigureAwait(false);

        ReadState missing;
        await using (var read = client.ReadStreamAsync(Direction.Forwards, "orders-never", StreamPosition.Start, cancellationToken: cancellationToken))
        {
            missing = await read.ReadState.ConfigureAwait(false);
        }

        await output.WriteLineAsync($"8. A read of a stream that never existed reports {missing}.").ConfigureAwait(false);
        await output.WriteLineAsync("9. Dropping the database.").ConfigureAwait(false);

        return new Report(
            stream,
            appended.Revision,
            head ?? throw new InvalidOperationException("The read reported no head."),
            forwards,
            backwards,
            correlation,
            conflictActual,
            retried.Revision,
            missing);
    }

    private static EventData Event(string type, string json, JsonObject? metadata = null) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes(json), metadata);
}
