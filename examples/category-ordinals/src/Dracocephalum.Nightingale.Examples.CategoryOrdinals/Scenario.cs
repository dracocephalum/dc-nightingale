using System.Text;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Common;

namespace Dracocephalum.Nightingale.Examples.CategoryOrdinals;

/// <summary>
/// The virtual streams under ordinal numbering, step by step, against a server whose store was
/// initialized with ordinals: a category read whose numbers are dense, so its bounds are a count;
/// an event-type read the same way; a subscription that is confirmed with an ordinal and delivers
/// the next one; and a deleted stream leaving holes the reader skips and never reuses. The program
/// prints the steps; the test project asserts the report. Both run the same code, so the sample
/// can never drift from what is verified.
/// </summary>
public static class Scenario
{
    private const string Category = "$ce-orders";

    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Ordinals">The ordinals the category read returned, with the position each sits at.</param>
    /// <param name="OrdinalHead">The bounds the category read reported.</param>
    /// <param name="ByType">The ordinals the event-type read returned.</param>
    /// <param name="ConfirmedHead">The ordinal the category subscription was confirmed with.</param>
    /// <param name="LiveOrdinal">The ordinal the subscription delivered live.</param>
    /// <param name="AfterDelete">The ordinals the category read returned after a stream was deleted.</param>
    /// <param name="AfterDeleteHead">The bounds after the delete, holes included.</param>
    public sealed record Report(
        IReadOnlyList<(long Ordinal, long Position)> Ordinals,
        StreamHead OrdinalHead,
        IReadOnlyList<long> ByType,
        long ConfirmedHead,
        long LiveOrdinal,
        IReadOnlyList<long> AfterDelete,
        StreamHead AfterDeleteHead);

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

        await using var server = await ExampleServer.StartAsync(
            database.ConnectionString,
            cancellationToken,
            options =>
            {
                options.Store.Ordinals = true;
                options.Deletion.AllowDelete = true;
            }).ConfigureAwait(false);
        await output.WriteLineAsync($"2. Server listening at {server.Address}; store initialized with ordinals, and deletion allowed for step 8.").ConfigureAwait(false);

        await using var connection = server.Connect();
        var client = connection.Client;

        await client.AppendToStreamAsync("orders-1", StreamState.NoStream, [Event("order_placed"), Event("order_paid")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("shipments-1", StreamState.NoStream, [Event("shipment_dispatched")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("orders-2", StreamState.NoStream, [Event("order_placed")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("3. Appended to orders-1, shipments-1 and orders-2: four events at positions 1 to 4, three of them orders.").ConfigureAwait(false);

        var (ordinals, ordinalHead) = await ReadNumberedAsync(client, Category, expectedLast: 2, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"4. Read {Category} by ordinal: {string.Join(", ", ordinals.Select(pair => $"{pair.Ordinal} at position {pair.Position}"))}; bounds {ordinalHead.First}..{ordinalHead.Last}, dense, so the count is {ordinalHead.Last - ordinalHead.First + 1} by subtraction.").ConfigureAwait(false);

        var (byType, _) = await ReadNumberedAsync(client, "$et-order_placed", expectedLast: 1, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"5. Read $et-order_placed by ordinal: {string.Join(", ", byType.Select(pair => pair.Ordinal))}, numbered on its own.").ConfigureAwait(false);

        await using var subscription = client.SubscribeToStreamAsync(Category, StreamPosition.End, Numbering.Ordinal, cancellationToken);
        var messages = subscription.Messages.GetAsyncEnumerator(cancellationToken);
        var confirmed = await subscription.Confirmed.ConfigureAwait(false);
        await output.WriteLineAsync($"6. Subscribed to {Category} by ordinal from the end: confirmed at ordinal {confirmed.Head}.").ConfigureAwait(false);

        await client.DeleteStreamAsync("orders-1", StreamState.Any, cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("orders-3", StreamState.NoStream, [Event("order_placed")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("7. Deleted orders-1, whose two events held ordinals 0 and 1, then appended to orders-3.").ConfigureAwait(false);

        long? live = null;
        while (live is null && await messages.MoveNextAsync().ConfigureAwait(false))
        {
            if (messages.Current is SubscriptionMessage.Recorded recorded)
            {
                live = recorded.Record.Ordinal;
            }
        }

        await output.WriteLineAsync($"8. The subscription delivered orders-3's event as ordinal {live}: the next number, never one of the deleted stream's.").ConfigureAwait(false);

        var (afterDelete, afterDeleteHead) = await ReadNumberedAsync(client, Category, expectedLast: 3, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"9. Read {Category} by ordinal again: {string.Join(", ", afterDelete.Select(pair => pair.Ordinal))}; bounds {afterDeleteHead.First}..{afterDeleteHead.Last} still count the holes, which the reader skips.").ConfigureAwait(false);

        await messages.DisposeAsync().ConfigureAwait(false);
        await output.WriteLineAsync("10. Disposed the subscription; dropping the database.").ConfigureAwait(false);

        return new Report(
            ordinals,
            ordinalHead,
            byType.Select(pair => pair.Ordinal).ToList(),
            confirmed.Head,
            live ?? throw new InvalidOperationException("The subscription ended before delivering the event."),
            afterDelete.Select(pair => pair.Ordinal).ToList(),
            afterDeleteHead);
    }

    private static EventData Event(string type) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    /// <summary>
    /// Reads a virtual stream by ordinal once the numberer, which runs behind the store's head on
    /// its own cadence, has reached the expected last ordinal.
    /// </summary>
    private static async Task<(List<(long Ordinal, long Position)> Events, StreamHead Head)> ReadNumberedAsync(NightingaleClient client, string stream, long expectedLast, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, numbering: Numbering.Ordinal, cancellationToken: timeout.Token);
            var head = await read.Head.ConfigureAwait(false);
            if (head is not null && head.Last >= expectedLast)
            {
                var events = new List<(long Ordinal, long Position)>();
                await foreach (var record in read.WithCancellation(timeout.Token).ConfigureAwait(false))
                {
                    events.Add((record.Ordinal ?? throw new InvalidOperationException("An ordinal read returned an event without its ordinal."), record.Position));
                }

                return (events, head);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false);
        }
    }
}
