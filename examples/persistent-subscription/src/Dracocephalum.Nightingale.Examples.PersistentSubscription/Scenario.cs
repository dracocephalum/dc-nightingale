using System.Text;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Common;

namespace Dracocephalum.Nightingale.Examples.PersistentSubscription;

/// <summary>
/// A persistent-subscription group against a Nightingale server, step by step: create it over a
/// stream, consume with acknowledgements, have one event retried and then parked, replay that one
/// parked message by itself, come back and resume from the checkpoint, and delete the group. The
/// program prints the steps; the test project asserts the report.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Delivered">Event types delivered by the first consumer, redeliveries included.</param>
    /// <param name="RetryCountOnRedelivery">The retry count the redelivered event carried.</param>
    /// <param name="Replayed">How many parked messages the replay put back.</param>
    /// <param name="ReplayedType">The type of the event the second consumer received first, the replayed one.</param>
    /// <param name="CheckpointOnReturn">The checkpoint the second consumer was confirmed with.</param>
    public sealed record Report(
        IReadOnlyList<string> Delivered,
        int RetryCountOnRedelivery,
        int Replayed,
        string ReplayedType,
        long CheckpointOnReturn);

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
        const string stream = "orders-1";
        const string group = "billing";

        await client.AppendToStreamAsync(stream, StreamState.NoStream, [Event("order_placed"), Event("order_paid"), Event("order_shipped")], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"3. Appended three events to {stream}.").ConfigureAwait(false);

        var settings = GroupSettings.Default with { Start = StreamPosition.Start, MaxRetryCount = 1, CheckpointUpperBound = 1, CheckpointLowerBound = 1 };
        await client.CreatePersistentSubscriptionAsync(stream, group, settings, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"4. Created group {group} over {stream} from its start, one retry before parking.").ConfigureAwait(false);

        var delivered = new List<string>();
        var retryOnRedelivery = -1;
        await using (var subscription = client.SubscribeToPersistentSubscriptionAsync(stream, group, bufferSize: 5, cancellationToken))
        {
            var confirmed = await subscription.Confirmed.ConfigureAwait(false);
            await output.WriteLineAsync($"5. Consumer connected; the group's checkpoint is {confirmed.Checkpoint}, nothing acknowledged yet.").ConfigureAwait(false);

            var messages = subscription.GetAsyncEnumerator(cancellationToken);
            for (var seen = 0; seen < 4 && await messages.MoveNextAsync().ConfigureAwait(false); seen++)
            {
                var message = messages.Current;
                delivered.Add(message.Record.Type);
                switch (message.Record.Type)
                {
                    case "order_paid" when message.RetryCount == 0:
                        await subscription.NackAsync(NackAction.Retry, "payment gateway busy", message.Record.Id).ConfigureAwait(false);
                        break;
                    case "order_paid":
                        retryOnRedelivery = message.RetryCount;
                        await subscription.NackAsync(NackAction.Park, "payment gateway down", message.Record.Id).ConfigureAwait(false);
                        break;
                    default:
                        await subscription.AckAsync(message.Record.Id).ConfigureAwait(false);
                        break;
                }
            }

            await messages.DisposeAsync().ConfigureAwait(false);
            await output.WriteLineAsync($"6. Delivered {string.Join(", ", delivered)}: all three were in flight before the payment was refused, so it came back after the shipment with retry count {retryOnRedelivery}, and was parked on the second refusal.").ConfigureAwait(false);
        }

        string replayedType;
        long checkpointOnReturn;
        int replayed;
        await using (var subscription = await ReconnectAsync(client, stream, group, cancellationToken).ConfigureAwait(false))
        {
            checkpointOnReturn = (await subscription.Confirmed.ConfigureAwait(false)).Checkpoint;
            await output.WriteLineAsync($"7. A new consumer was confirmed at checkpoint {checkpointOnReturn}; everything is acknowledged or parked, so it waits.").ConfigureAwait(false);

            replayed = await ReplayOnceParkedAsync(client, stream, group, position: 1, cancellationToken).ConfigureAwait(false);
            var messages = subscription.GetAsyncEnumerator(cancellationToken);
            if (!await messages.MoveNextAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("The replayed message was not delivered.");
            }

            replayedType = messages.Current.Record.Type;
            await subscription.AckAsync(messages.Current.Record.Id).ConfigureAwait(false);
            await messages.DisposeAsync().ConfigureAwait(false);
            await output.WriteLineAsync($"8. Replayed the one parked message at revision 1 while the consumer was connected: {replayed} moved to the group's outbox, and the consumer received the {replayedType} at once and acknowledged it.").ConfigureAwait(false);
        }

        await client.DeletePersistentSubscriptionAsync(stream, group, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("9. Deleted the group; dropping the database.").ConfigureAwait(false);

        return new Report(delivered, retryOnRedelivery, replayed, replayedType, checkpointOnReturn);
    }

    private static EventData Event(string type) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    /// <summary>
    /// Connects to the group again. The server frees a consumer's place a moment after its call
    /// ends, so a consumer that reconnects at once may be told the place is still taken; it tries
    /// again shortly, as a consumer resuming after a dropped connection would.
    /// </summary>
    private static async Task<Client.PersistentSubscription> ReconnectAsync(NightingaleClient client, string stream, string group, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var subscription = client.SubscribeToPersistentSubscriptionAsync(stream, group, bufferSize: 5, cancellationToken);
            try
            {
                await subscription.Confirmed.ConfigureAwait(false);
                return subscription;
            }
            catch (ConsumerLimitReachedException) when (attempt < 50)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Replays one parked message. A refusal is one-way: it is on the wire when NackAsync returns,
    /// and the server parks the message a moment later, so a replay asked for at once may find no
    /// parked message yet; it asks again shortly, as an operator's tool would.
    /// </summary>
    private static async Task<int> ReplayOnceParkedAsync(NightingaleClient client, string stream, string group, long position, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await client.ReplayParkedMessagesAsync(stream, group, position, cancellationToken).ConfigureAwait(false);
            }
            catch (ParkedMessageNotFoundException) when (attempt < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
