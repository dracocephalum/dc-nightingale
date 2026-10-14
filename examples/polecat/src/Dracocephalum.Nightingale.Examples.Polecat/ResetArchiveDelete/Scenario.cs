using System.Text;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Server;

namespace Dracocephalum.Nightingale.Examples.Polecat.ResetArchiveDelete;

/// <summary>
/// The three ways a stream is taken away, against a Nightingale server, step by step: a reset
/// that hides a stream from its reads until the next append, which continues its revisions; an
/// archive that hides it from every read and refuses appends; a delete that removes it and frees
/// its name; a stale expected revision refused; and a server with the default settings refusing
/// to archive while it still resets. The program prints the steps; the test project asserts the
/// report.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Stream">The stream that was reset, archived, deleted and used again.</param>
    /// <param name="CategoryBefore">How many events the category stream held before anything was taken away.</param>
    /// <param name="StateAfterReset">What a read reported after the reset.</param>
    /// <param name="CategoryAfterReset">How many events the category stream held after the reset.</param>
    /// <param name="ResetReported">Whether the stream's metadata reported it reset.</param>
    /// <param name="RecreatedRevision">The revision of the append that brought the stream back, expecting no stream.</param>
    /// <param name="ReadAfterRecreation">The revisions a read returned once the stream was back.</param>
    /// <param name="ConflictActualRevision">The actual revision reported when a stale archive was refused.</param>
    /// <param name="ReadAfterArchive">The exception a read of the archived stream threw.</param>
    /// <param name="CategoryAfterArchive">How many events the category stream held after the archive.</param>
    /// <param name="AppendAfterArchive">The exception an append to the archived stream threw.</param>
    /// <param name="StateAfterDelete">What a read reported after the delete.</param>
    /// <param name="ReusedRevision">The revision the first append to the freed name reported.</param>
    /// <param name="DefaultServerRefusal">The exception the run's server, with the default settings, threw on archive.</param>
    /// <param name="DefaultServerReset">Whether the run's server, with the default settings, still reset a stream.</param>
    public sealed record Report(
        string Stream,
        int CategoryBefore,
        ReadState StateAfterReset,
        int CategoryAfterReset,
        bool ResetReported,
        long RecreatedRevision,
        IReadOnlyList<long> ReadAfterRecreation,
        long ConflictActualRevision,
        string ReadAfterArchive,
        int CategoryAfterArchive,
        string AppendAfterArchive,
        ReadState StateAfterDelete,
        long ReusedRevision,
        string DefaultServerRefusal,
        bool DefaultServerReset);

    /// <summary>Runs the scenario.</summary>
    /// <param name="run">The run the scenario is part of: its database, its server, and a tenant for the scenario.</param>
    /// <param name="output">Where the steps are narrated.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public static async Task<Report> RunAsync(ExampleRun run, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(output);

        var tenant = await run.CreateTenantAsync("reset-archive-delete", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"1. Created the tenant {tenant.Name} for this scenario; its streams live there and nowhere else.").ConfigureAwait(false);

        await using var server = await run.StartServerAsync(cancellationToken, options => options.Deletion.Allowed = DeletionLevel.Delete).ConfigureAwait(false);
        await output.WriteLineAsync($"2. Started a server at {server.Address} with the deletion level at Delete, so archive and delete are allowed, which the run's server, with the default settings, does not allow.").ConfigureAwait(false);

        await using var connection = server.Connect(tenant);
        var client = connection.Client;
        const string stream = "orders-1";

        await client.AppendToStreamAsync(stream, StreamState.NoStream, [CreateEvent("order_placed"), CreateEvent("order_paid")], cancellationToken).ConfigureAwait(false);
        var appended = await client.AppendToStreamAsync("orders-2", StreamState.NoStream, [CreateEvent("order_placed")], cancellationToken).ConfigureAwait(false);
        await ExampleMark.WaitUntilCoversAsync(client, appended.Position, cancellationToken).ConfigureAwait(false);
        var before = await CountAsync(client, "$ce-orders", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"3. Appended two events to {stream} and one to orders-2; once the store's mark covered them, $ce-orders holds {before}.").ConfigureAwait(false);

        await client.ResetStreamAsync(stream, StreamState.StreamRevision(1), cancellationToken).ConfigureAwait(false);
        var afterReset = await ReadStateAsync(client, stream, cancellationToken).ConfigureAwait(false);
        var categoryAfterReset = await CountAsync(client, "$ce-orders", cancellationToken).ConfigureAwait(false);
        var metadata = await client.GetStreamMetadataAsync(stream, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"4. Reset {stream}: a read reports {afterReset}, while $ce-orders still holds {categoryAfterReset}; its metadata says reset {metadata.Reset}, continuing from revision {metadata.Metadata.TruncateBefore}. Nothing was removed.").ConfigureAwait(false);

        var recreated = await client.AppendToStreamAsync(stream, StreamState.NoStream, [CreateEvent("order_placed")], cancellationToken).ConfigureAwait(false);
        var readAfterRecreation = await ReadRevisionsAsync(client, stream, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"5. Appended to {stream} expecting no stream, as to a new one: revision {recreated.Revision}, and a read returns revisions {string.Join(", ", readAfterRecreation)}; the two before the reset stay hidden.").ConfigureAwait(false);

        long conflictActual;
        try
        {
            await client.ArchiveStreamAsync(stream, StreamState.StreamRevision(0), cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A stale archive was accepted.");
        }
        catch (RevisionConflictException conflict)
        {
            conflictActual = conflict.ActualRevision;
            await output.WriteLineAsync($"6. An archive expecting revision 0 was refused; the stream is at revision {conflictActual}.").ConfigureAwait(false);
        }

        await client.ArchiveStreamAsync(stream, StreamState.StreamRevision(conflictActual), cancellationToken).ConfigureAwait(false);
        string readAfterArchive;
        await using (var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken))
        {
            readAfterArchive = await CaptureFailureAsync(async () => await read.ReadState.ConfigureAwait(false)).ConfigureAwait(false);
        }

        var categoryAfterArchive = await CountAsync(client, "$ce-orders", cancellationToken).ConfigureAwait(false);
        var appendAfterArchive = await CaptureFailureAsync(() => client.AppendToStreamAsync(stream, StreamState.Any, [CreateEvent("order_shipped")], cancellationToken)).ConfigureAwait(false);
        await output.WriteLineAsync($"7. Archived {stream}: a read fails with {readAfterArchive}, $ce-orders holds {categoryAfterArchive}, an append fails with {appendAfterArchive}.").ConfigureAwait(false);

        await client.DeleteStreamAsync(stream, StreamState.Any, cancellationToken).ConfigureAwait(false);
        var afterDelete = await ReadStateAsync(client, stream, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"8. Deleted {stream}: it and its events are gone, and a read reports {afterDelete}, as for a stream that never existed.").ConfigureAwait(false);

        var reused = await client.AppendToStreamAsync(stream, StreamState.NoStream, [CreateEvent("order_placed")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"9. Appended to {stream} again as a new stream; revision {reused.Revision}.").ConfigureAwait(false);

        await using var defaultConnection = run.Server.Connect(tenant);
        var defaultClient = defaultConnection.Client;
        var refusal = await CaptureFailureAsync(() => defaultClient.ArchiveStreamAsync(stream, StreamState.Any, cancellationToken)).ConfigureAwait(false);
        await defaultClient.ResetStreamAsync("orders-2", StreamState.Any, cancellationToken).ConfigureAwait(false);
        var defaultReset = (await defaultClient.GetStreamMetadataAsync("orders-2", cancellationToken).ConfigureAwait(false)).Reset;
        await output.WriteLineAsync($"10. The run's server, with the default settings, refused to archive {stream} with {refusal}, and reset orders-2 all the same: a reset hides and removes nothing.").ConfigureAwait(false);

        return new Report(stream, before, afterReset, categoryAfterReset, metadata.Reset, recreated.Revision, readAfterRecreation, conflictActual, readAfterArchive, categoryAfterArchive, appendAfterArchive, afterDelete, reused.Revision, refusal, defaultReset);
    }

    private static async Task<ReadState> ReadStateAsync(NightingaleClient client, string stream, CancellationToken cancellationToken)
    {
        await using var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken);
        return await read.ReadState.ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<long>> ReadRevisionsAsync(NightingaleClient client, string stream, CancellationToken cancellationToken)
    {
        var revisions = new List<long>();
        await using var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken);
        await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(record.Revision);
        }

        return revisions;
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
