using System.Text;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Common;

namespace Dracocephalum.Nightingale.Examples.CategoryStream;

/// <summary>
/// The virtual streams against a Nightingale server, step by step: a category stream and an
/// event-type stream read across several plain streams, and a subscription to the category that
/// sees what lands in its streams and nothing else. The program prints the steps; the test project
/// asserts the report. Both run the same code, so the sample can never drift from what is
/// verified.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Category">Stream and type of every event the category stream returned, in position order.</param>
    /// <param name="CategoryHead">The bounds the category read reported.</param>
    /// <param name="ByType">Streams of every event the event-type stream returned, in position order.</param>
    /// <param name="ConfirmedHead">The head the category subscription was confirmed with.</param>
    /// <param name="Live">Stream and type of every event the category subscription delivered live.</param>
    public sealed record Report(
        IReadOnlyList<string> Category,
        StreamHead CategoryHead,
        IReadOnlyList<string> ByType,
        long ConfirmedHead,
        IReadOnlyList<string> Live);

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

        await client.AppendToStreamAsync("orders-1", StreamState.NoStream, [Event("order_placed"), Event("order_paid")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("shipments-1", StreamState.NoStream, [Event("shipment_dispatched")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("orders-2", StreamState.NoStream, [Event("order_placed")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("3. Appended to orders-1, shipments-1 and orders-2: four events, two categories, three types.").ConfigureAwait(false);

        var category = new List<string>();
        StreamHead? categoryHead;
        await using (var read = client.ReadStreamAsync(Direction.Forwards, "$ce-orders", StreamPosition.Start, cancellationToken: cancellationToken))
        {
            categoryHead = await read.Head.ConfigureAwait(false);
            await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                category.Add(record.Stream + ":" + record.Type);
            }
        }

        await output.WriteLineAsync($"4. Read $ce-orders: {string.Join(", ", category)}; bounds {categoryHead?.First}..{categoryHead?.Last}, the category's own, not the store's.").ConfigureAwait(false);

        var byType = new List<string>();
        await using (var read = client.ReadStreamAsync(Direction.Forwards, "$et-order_placed", StreamPosition.Start, cancellationToken: cancellationToken))
        {
            await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                byType.Add(record.Stream);
            }
        }

        await output.WriteLineAsync($"5. Read $et-order_placed: one from each of {string.Join(", ", byType)}.").ConfigureAwait(false);

        await using var subscription = client.SubscribeToStreamAsync("$ce-orders", StreamPosition.End, cancellationToken: cancellationToken);
        var messages = subscription.Messages.GetAsyncEnumerator(cancellationToken);
        var confirmed = await subscription.Confirmed.ConfigureAwait(false);
        await output.WriteLineAsync($"6. Subscribed to $ce-orders from the end: confirmed at the category's head, position {confirmed.Head}.").ConfigureAwait(false);

        await client.AppendToStreamAsync("shipments-1", StreamState.StreamRevision(0), [Event("shipment_delivered")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("orders-3", StreamState.NoStream, [Event("order_placed")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("7. Appended to shipments-1, which the subscription must not see, then to orders-3, which it must.").ConfigureAwait(false);

        var live = new List<string>();
        while (await messages.MoveNextAsync().ConfigureAwait(false))
        {
            if (messages.Current is SubscriptionMessage.Recorded recorded)
            {
                live.Add(recorded.Record.Stream + ":" + recorded.Record.Type);
            }
            else if (messages.Current is SubscriptionMessage.CaughtUp && live.Count > 0)
            {
                break;
            }
        }

        await output.WriteLineAsync($"8. The subscription delivered {string.Join(", ", live)} and nothing from shipments.").ConfigureAwait(false);

        await messages.DisposeAsync().ConfigureAwait(false);
        await output.WriteLineAsync("9. Disposed the subscription; dropping the database.").ConfigureAwait(false);

        return new Report(
            category,
            categoryHead ?? throw new InvalidOperationException("The read reported no bounds."),
            byType,
            confirmed.Head,
            live);
    }

    private static EventData Event(string type) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));
}
