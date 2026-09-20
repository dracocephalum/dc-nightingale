using System.Text;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Common;

namespace Dracocephalum.Nightingale.Examples.CatchUpSubscription;

/// <summary>
/// Two catch-up subscriptions against a Nightingale server, step by step: one to a stream from its
/// start, which catches up on what is there and then goes live, and one to <c>$all</c> from its
/// end, which sees only what comes after. The program prints the steps; the test project asserts
/// the report. Both run the same code, so the sample can never drift from what is verified.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Stream">The stream the run subscribed to.</param>
    /// <param name="ConfirmedHead">The head the stream subscription was confirmed with.</param>
    /// <param name="CaughtUp">Event types delivered before the subscription reported it had caught up.</param>
    /// <param name="CaughtUpHead">The head the caught-up note carried.</param>
    /// <param name="AllConfirmedHead">The head the <c>$all</c> subscription was confirmed with.</param>
    /// <param name="Live">Event types the stream subscription delivered live.</param>
    /// <param name="AllLive">Stream and type of every event the <c>$all</c> subscription delivered live, in position order.</param>
    public sealed record Report(
        string Stream,
        long ConfirmedHead,
        IReadOnlyList<string> CaughtUp,
        long CaughtUpHead,
        long AllConfirmedHead,
        IReadOnlyList<string> Live,
        IReadOnlyList<string> AllLive);

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
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orders = "orders-" + suffix;
        var shipments = "shipments-" + suffix;

        await client.AppendToStreamAsync(orders, StreamState.NoStream, [Event("order_placed"), Event("order_paid")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"3. Appended two events to {orders} before anyone subscribed.").ConfigureAwait(false);

        await using var streamSubscription = client.SubscribeToStreamAsync(orders, StreamPosition.Start, cancellationToken: cancellationToken);
        var streamMessages = streamSubscription.Messages.GetAsyncEnumerator(cancellationToken);
        var confirmed = await streamSubscription.Confirmed.ConfigureAwait(false);
        var caughtUp = new List<string>();
        var caughtUpNote = await ReadUntil<SubscriptionMessage.CaughtUp>(streamMessages, caughtUp).ConfigureAwait(false);
        await output.WriteLineAsync($"4. Subscribed to {orders} from the start: confirmed at head {confirmed.Head}; caught up after {string.Join(", ", caughtUp)}; head {caughtUpNote.Head}.").ConfigureAwait(false);

        await using var allSubscription = client.SubscribeToAllAsync(StreamPosition.End, cancellationToken);
        var allMessages = allSubscription.Messages.GetAsyncEnumerator(cancellationToken);
        var allConfirmed = await allSubscription.Confirmed.ConfigureAwait(false);
        await ReadUntil<SubscriptionMessage.CaughtUp>(allMessages, []).ConfigureAwait(false);
        await output.WriteLineAsync($"5. Subscribed to $all from the end: confirmed at position {allConfirmed.Head}; nothing to catch up on.").ConfigureAwait(false);

        await client.AppendToStreamAsync(orders, StreamState.StreamRevision(1), [Event("order_shipped")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync(shipments, StreamState.NoStream, [Event("shipment_dispatched")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"6. Appended one event to {orders} and one to {shipments} while both subscriptions were live.").ConfigureAwait(false);

        var live = new List<string>();
        await ReadUntil<SubscriptionMessage.CaughtUp>(streamMessages, live).ConfigureAwait(false);
        await output.WriteLineAsync($"7. The stream subscription delivered {string.Join(", ", live)} and caught up again.").ConfigureAwait(false);

        var allLive = new List<string>();
        while (allLive.Count < 2)
        {
            await ReadUntil<SubscriptionMessage.CaughtUp>(allMessages, allLive, describe: record => record.Stream + ":" + record.Type).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"8. The $all subscription delivered {string.Join(", ", allLive)} in position order.").ConfigureAwait(false);

        await streamMessages.DisposeAsync().ConfigureAwait(false);
        await allMessages.DisposeAsync().ConfigureAwait(false);
        await output.WriteLineAsync("9. Disposed both subscriptions; dropping the database.").ConfigureAwait(false);

        return new Report(orders, confirmed.Head, caughtUp, caughtUpNote.Head, allConfirmed.Head, live, allLive);
    }

    /// <summary>Reads messages, collecting the events, until a message of the given kind arrives.</summary>
    private static async Task<T> ReadUntil<T>(IAsyncEnumerator<SubscriptionMessage> messages, List<string> events, Func<EventRecord, string>? describe = null)
        where T : SubscriptionMessage
    {
        while (await messages.MoveNextAsync().ConfigureAwait(false))
        {
            switch (messages.Current)
            {
                case SubscriptionMessage.Recorded recorded:
                    events.Add(describe is null ? recorded.Record.Type : describe(recorded.Record));
                    break;
                case T note:
                    return note;
                default:
                    break;
            }
        }

        throw new InvalidOperationException("The subscription ended before the expected note.");
    }

    private static EventData Event(string type) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));
}
