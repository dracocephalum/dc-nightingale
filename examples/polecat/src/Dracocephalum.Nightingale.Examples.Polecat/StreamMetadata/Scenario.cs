using System.Text;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;

namespace Dracocephalum.Nightingale.Examples.Polecat.StreamMetadata;

/// <summary>
/// A stream's metadata against a Nightingale server, step by step: metadata set before the
/// stream's first event and read back, a stale set refused on the metadata stream's own revision,
/// the history read as the <c>$metadata</c> events of <c>$$orders-1</c>, <c>$acl</c> refused, and
/// the metadata events in <c>$all</c> and <c>$mde</c> but never in <c>$de</c>, and a <c>$maxCount</c>
/// applied to a read of its stream and to nothing else. The program prints the steps; the test
/// project asserts the report.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Stream">The stream whose metadata was set.</param>
    /// <param name="FirstSet">The metadata stream's revision after the first set.</param>
    /// <param name="ReadBeforeAppend">What a read of the stream reported once its metadata was set and before its first event.</param>
    /// <param name="ConflictActualRevision">The actual revision reported when a set expecting no metadata was refused.</param>
    /// <param name="Final">The metadata as last set, read back.</param>
    /// <param name="History">The types of the metadata stream's events, in order.</param>
    /// <param name="AclRefusal">The exception a set carrying <c>$acl</c> threw.</param>
    /// <param name="All">How many events of the tenant <c>$all</c> holds.</param>
    /// <param name="DataEvents">How many <c>$de</c> holds.</param>
    /// <param name="MetadataEvents">How many <c>$mde</c> holds.</param>
    /// <param name="Kept">The revisions a read of the second stream returned under its <c>$maxCount</c>.</param>
    /// <param name="Category">How many events <c>$ce-orders</c> holds, both streams' alike.</param>
    public sealed record Report(
        string Stream,
        long FirstSet,
        ReadState ReadBeforeAppend,
        long ConflictActualRevision,
        StreamMetadataResult Final,
        IReadOnlyList<string> History,
        string AclRefusal,
        int All,
        int DataEvents,
        int MetadataEvents,
        IReadOnlyList<long> Kept,
        int Category);

    /// <summary>Runs the scenario.</summary>
    /// <param name="run">The run the scenario is part of: its database, its server, and a tenant for the scenario.</param>
    /// <param name="output">Where the steps are narrated.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public static async Task<Report> RunAsync(ExampleRun run, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(output);

        var tenant = await run.CreateTenantAsync("stream-metadata", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"1. Created the tenant {tenant.Name} for this scenario; its streams, their metadata and $all live there and nowhere else.").ConfigureAwait(false);

        await using var connection = run.Server.Connect(tenant);
        var client = connection.Client;
        const string stream = "orders-1";

        var retention = new Nightingale.StreamMetadata { MaxAge = TimeSpan.FromDays(30), Custom = new JsonObject { ["owner"] = "billing" } };
        var first = await client.SetStreamMetadataAsync(stream, StreamState.NoStream, retention, cancellationToken).ConfigureAwait(false);
        ReadState beforeAppend;
        await using (var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken))
        {
            beforeAppend = await read.ReadState.ConfigureAwait(false);
        }

        await output.WriteLineAsync($"2. Set {stream}'s metadata before its first event: a 30-day $maxAge and an owner of the application's; the metadata stream $${stream} is at revision {first.Revision}, and {stream} itself still reads as {beforeAppend}.").ConfigureAwait(false);

        await client.AppendToStreamAsync(stream, StreamState.NoStream, [CreateEvent("order_placed"), CreateEvent("order_paid")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"3. Appended two events to {stream}; the metadata stream's revision and the stream's are counted apart.").ConfigureAwait(false);

        long conflictActual;
        try
        {
            await client.SetStreamMetadataAsync(stream, StreamState.NoStream, new Nightingale.StreamMetadata { MaxCount = 1 }, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A set expecting no metadata was accepted over existing metadata.");
        }
        catch (RevisionConflictException conflict)
        {
            conflictActual = conflict.ActualRevision;
            await output.WriteLineAsync($"4. A set expecting no metadata was refused: {conflict.Stream} is at revision {conflictActual}.").ConfigureAwait(false);
        }

        var second = await client.SetStreamMetadataAsync(stream, StreamState.StreamRevision(first.Revision), retention with { MaxCount = 10 }, cancellationToken).ConfigureAwait(false);
        var final = await client.GetStreamMetadataAsync(stream, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"5. Set it again expecting revision {first.Revision}, adding a $maxCount of 10; read back at revision {final.Revision}: $maxAge {final.Metadata.MaxAge?.TotalDays} days, $maxCount {final.Metadata.MaxCount}, owner {final.Metadata.Custom?["owner"]}.").ConfigureAwait(false);

        var history = new List<string>();
        await using (var read = client.ReadStreamAsync(Direction.Forwards, StreamNames.ToMetadataStream(stream), StreamPosition.Start, cancellationToken: cancellationToken))
        {
            await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                history.Add(record.Type);
            }
        }

        await output.WriteLineAsync($"6. Read $${stream} as a stream: {history.Count} events, {string.Join(", ", history)}, the history of who changed the stream's retention.").ConfigureAwait(false);

        var aclRefusal = await CaptureFailureAsync(() => client.SetStreamMetadataAsync(stream, StreamState.Any, new Nightingale.StreamMetadata { Custom = new JsonObject { ["$acl"] = new JsonObject { ["$r"] = "$all" } } }, cancellationToken)).ConfigureAwait(false);
        await output.WriteLineAsync($"7. A set carrying $acl was refused with {aclRefusal}: this server does not enforce stream access control lists, and does not store one as if it did.").ConfigureAwait(false);

        await ExampleMark.WaitUntilCoversAsync(client, second.Position, cancellationToken).ConfigureAwait(false);
        var all = await CountAsync(client, StreamNames.All, cancellationToken).ConfigureAwait(false);
        var dataEvents = await CountAsync(client, StreamNames.DataEvents, cancellationToken).ConfigureAwait(false);
        var metadataEvents = await CountAsync(client, StreamNames.MetadataEvents, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"8. The tenant's $all holds {all} events, the metadata events among them; $de, the data events, holds {dataEvents}; $mde, the metadata events, holds {metadataEvents}.").ConfigureAwait(false);

        const string trimmed = "orders-2";
        await client.AppendToStreamAsync(trimmed, StreamState.NoStream, [CreateEvent("order_placed"), CreateEvent("order_paid"), CreateEvent("order_packed"), CreateEvent("order_shipped"), CreateEvent("order_delivered")], cancellationToken).ConfigureAwait(false);
        var trim = await client.SetStreamMetadataAsync(trimmed, StreamState.NoStream, new Nightingale.StreamMetadata { MaxCount = 2 }, cancellationToken).ConfigureAwait(false);
        var kept = new List<long>();
        await using (var read = client.ReadStreamAsync(Direction.Forwards, trimmed, StreamPosition.Start, cancellationToken: cancellationToken))
        {
            await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                kept.Add(record.Revision);
            }
        }

        await ExampleMark.WaitUntilCoversAsync(client, trim.Position, cancellationToken).ConfigureAwait(false);
        var category = await CountAsync(client, "$ce-orders", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"9. Appended five events to {trimmed} and set a $maxCount of 2: a read from the start returns revisions {string.Join(", ", kept)}, the last two; $ce-orders still holds all {category} events of the category, since metadata is the business of the stream's own reads.").ConfigureAwait(false);

        return new Report(stream, first.Revision, beforeAppend, conflictActual, final, history, aclRefusal, all, dataEvents, metadataEvents, kept, category);
    }

    private static async Task<int> CountAsync(NightingaleClient client, string stream, CancellationToken cancellationToken)
    {
        var count = 0;
        await using var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken);
        await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            _ = record;
            count++;
        }

        return count;
    }

    /// <summary>Runs a call that is expected to fail and names the exception it failed with.</summary>
    private static async Task<string> CaptureFailureAsync(Func<Task> call)
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

    private static EventData CreateEvent(string type) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));
}
