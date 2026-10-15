using System.Text;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;

namespace Dracocephalum.Nightingale.Examples.Polecat.MultiStreamAppend;

/// <summary>
/// An append to several streams, against a Nightingale server, step by step: two accounts opened
/// in one commit; a transfer that debits one and credits the other in one commit, on the
/// condition that a stream it does not write, the sender's freeze, does not exist; a second
/// transfer decided on stale revisions, refused with both streams named; the next refused
/// once the sender is frozen, by its check; an account opened with its metadata in the same
/// commit; and a reset through metadata refused. The program prints the steps; the test project
/// asserts the report.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Opened">Each opened account and the revision its first event got, as <c>stream:revision</c>.</param>
    /// <param name="TransferInOrder">Whether the debit was positioned before the credit, the order the transfer gave them in.</param>
    /// <param name="StaleConflicts">The streams the transfer decided on stale revisions was refused for, as <c>index:stream</c>.</param>
    /// <param name="FrozenConflict">The condition the transfer from a frozen account failed, as <c>index:stream</c>.</param>
    /// <param name="WithMetadata">What the account opened with its metadata wrote, as <c>stream:revision</c>.</param>
    /// <param name="MaxCount">The <c>$maxCount</c> that account's metadata reads back with.</param>
    /// <param name="ResetRefusal">Why a reset through metadata was refused.</param>
    public sealed record Report(
        IReadOnlyList<string> Opened,
        bool TransferInOrder,
        IReadOnlyList<string> StaleConflicts,
        string FrozenConflict,
        IReadOnlyList<string> WithMetadata,
        long? MaxCount,
        string ResetRefusal);

    /// <summary>Runs the scenario.</summary>
    /// <param name="run">The run the scenario is part of: its database, its server, and a tenant for the scenario.</param>
    /// <param name="output">Where the steps are narrated.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public static async Task<Report> RunAsync(ExampleRun run, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(output);

        var tenant = await run.CreateTenantAsync("multi-stream-append", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"1. Created the tenant {tenant.Name} for this scenario.").ConfigureAwait(false);

        await using var connection = run.Server.Connect(tenant);
        var client = connection.Client;

        var opened = await client.AppendToStreamsAsync(
            [
                new StreamAppend.OfEvents("account-alice", StreamState.NoStream, [CreateEvent("account_opened", "{\"balance\":100}")]),
                new StreamAppend.OfEvents("account-bob", StreamState.NoStream, [CreateEvent("account_opened", "{\"balance\":0}")]),
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var openedText = opened.Select(result => $"{result.Stream}:{result.Revision}").ToList();
        await output.WriteLineAsync($"2. Opened two accounts in one commit: {string.Join(", ", openedText)}.").ConfigureAwait(false);

        StreamCheck[] notFrozen = [new StreamCheck("freeze-alice", StreamState.NoStream)];
        var transferred = await client.AppendToStreamsAsync(CreateTransfer(30, 0), notFrozen, cancellationToken).ConfigureAwait(false);
        var inOrder = transferred[0].Position < transferred[1].Position;
        await output.WriteLineAsync($"3. Moved 30 from alice to bob in one commit, on the condition that freeze-alice does not exist: the debit at position {transferred[0].Position}, the credit at {transferred[1].Position}, in the order given.").ConfigureAwait(false);

        var stale = await RefuseAsync(client, CreateTransfer(20, 0), notFrozen, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"4. Sent a second transfer, of 20, decided on the revisions read before the first, both accounts at 0: refused for {string.Join(" and ", stale)}, every stream that had moved on, and nothing was written.").ConfigureAwait(false);

        await client.AppendToStreamAsync("freeze-alice", StreamState.NoStream, [CreateEvent("account_frozen", "{\"reason\":\"review\"}")], cancellationToken).ConfigureAwait(false);
        var frozen = (await RefuseAsync(client, CreateTransfer(10, 1), notFrozen, cancellationToken).ConfigureAwait(false)).Single();
        await output.WriteLineAsync($"5. Froze alice's account, then sent the next transfer on current revisions: refused for {frozen}, the check, numbered after the two appends.").ConfigureAwait(false);

        var withMetadata = await client.AppendToStreamsAsync(
            [
                new StreamAppend.OfEvents("account-carol", StreamState.NoStream, [CreateEvent("account_opened", "{\"balance\":0}")]),
                new StreamAppend.OfMetadata("account-carol", StreamState.NoStream, new Nightingale.StreamMetadata { MaxCount = 1000 }),
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var withMetadataText = withMetadata.Select(result => $"{result.Stream}:{result.Revision}").ToList();
        var metadata = await client.GetStreamMetadataAsync("account-carol", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"6. Opened carol's account and set its metadata in one commit: {string.Join(", ", withMetadataText)}; its $maxCount reads back as {metadata.Metadata.MaxCount}.").ConfigureAwait(false);

        string resetRefusal;
        try
        {
            await client.AppendToStreamsAsync(
                [new StreamAppend.OfMetadata("account-carol", StreamState.Any, new Nightingale.StreamMetadata { TruncateBefore = long.MaxValue })],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A reset through metadata in an append to several streams was accepted.");
        }
        catch (ArgumentException refused)
        {
            resetRefusal = refused.Message;
        }

        await output.WriteLineAsync($"7. Tried to reset carol's account through its metadata inside an append to several streams: refused, \"{resetRefusal}\"").ConfigureAwait(false);

        return new Report(openedText, inOrder, stale, frozen, withMetadataText, metadata.Metadata.MaxCount, resetRefusal);
    }

    /// <summary>Sends an append the scenario expects refused, and gives back the conditions that failed, as <c>index:stream</c>.</summary>
    private static async Task<IReadOnlyList<string>> RefuseAsync(NightingaleClient client, StreamAppend[] appends, StreamCheck[] checks, CancellationToken cancellationToken)
    {
        try
        {
            await client.AppendToStreamsAsync(appends, checks, cancellationToken).ConfigureAwait(false);
        }
        catch (StreamConflictsException conflicts)
        {
            return conflicts.Conflicts.Select(conflict => $"{conflict.Index}:{conflict.Stream}").ToList();
        }

        throw new InvalidOperationException("An append the scenario expected refused was written.");
    }

    /// <summary>
    /// A transfer from alice to bob, decided on both accounts at a revision: the debit first, then
    /// the credit. New events each time, so it is a transfer of its own, never the retry of one; a
    /// retry sends the same events again.
    /// </summary>
    private static StreamAppend[] CreateTransfer(int amount, long revision) =>
    [
        new StreamAppend.OfEvents("account-alice", StreamState.StreamRevision(revision), [CreateEvent("money_sent", $"{{\"amount\":{amount},\"to\":\"bob\"}}")]),
        new StreamAppend.OfEvents("account-bob", StreamState.StreamRevision(revision), [CreateEvent("money_received", $"{{\"amount\":{amount},\"from\":\"alice\"}}")]),
    ];

    private static EventData CreateEvent(string type, string json) => new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes(json));
}
