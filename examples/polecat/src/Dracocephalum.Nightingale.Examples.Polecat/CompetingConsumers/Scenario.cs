using System.Text;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;

namespace Dracocephalum.Nightingale.Examples.Polecat.CompetingConsumers;

/// <summary>
/// Two consumers on one persistent-subscription group against a Nightingale server, step by
/// step: create the group pinned, so each stream's events go to one consumer, connect two
/// consumers and see the streams shared out between them, ask the server about the group and see
/// both consumers, change the group to round robin and see both calls end so they connect again,
/// then have one consumer leave while it holds an event and see the other receive it at once,
/// not counted as a retry. The program prints the steps; the test project asserts the report.
/// </summary>
public static class Scenario
{
    private const string Category = "$ce-orders";
    private const string Group = "billing";

    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="PinnedStreams">The distinct streams each of the two pinned consumers received, in connection order.</param>
    /// <param name="PinnedDelivered">How many events the two pinned consumers received between them.</param>
    /// <param name="Info">What the server said about the group while both consumers were connected and idle.</param>
    /// <param name="EndedWith">How each consumer's enumeration ended when the group was updated.</param>
    /// <param name="Updated">The group's settings after the change.</param>
    /// <param name="QuickReceived">The events the quick consumer received under round robin, as (revision, retry count).</param>
    /// <param name="HandedOver">Whether the event the slow consumer held was the last the quick one received.</param>
    public sealed record Report(
        IReadOnlyList<IReadOnlyList<string>> PinnedStreams,
        int PinnedDelivered,
        PersistentSubscriptionInfo Info,
        IReadOnlyList<string> EndedWith,
        GroupSettings Updated,
        IReadOnlyList<(long Revision, int RetryCount)> QuickReceived,
        bool HandedOver);

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

        var settings = GroupSettings.Default with { Start = StreamPosition.Start, ConsumerStrategy = ConsumerStrategy.Pinned };
        await client.CreatePersistentSubscriptionAsync(Category, Group, settings, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"3. Created group {Group} over {Category} from its start, pinned: a stream's events always go to the same consumer.").ConfigureAwait(false);

        // Two consumers, connected before anything is appended, so the streams are shared out
        // over both from the first event.
        var received = new[] { new List<PersistentSubscriptionMessage.Recorded>(), new List<PersistentSubscriptionMessage.Recorded>() };
        await using var first = await client.SubscribeToPersistentSubscriptionAsync(Category, Group, bufferSize: 10, cancellationToken).ConfigureAwait(false);
        await using var second = await client.SubscribeToPersistentSubscriptionAsync(Category, Group, bufferSize: 10, cancellationToken).ConfigureAwait(false);
        var consuming = new[] { ConsumeAsync(first, received[0], cancellationToken), ConsumeAsync(second, received[1], cancellationToken) };
        await output.WriteLineAsync("4. Two consumers connected to the group, each holding up to ten events.").ConfigureAwait(false);

        for (var order = 1; order <= 4; order++)
        {
            await client.AppendToStreamAsync($"orders-{order}", StreamState.NoStream, [CreateEvent("order_placed"), CreateEvent("order_paid")], cancellationToken).ConfigureAwait(false);
        }

        await UntilAsync(() => received.Sum(list => list.Count) == 8, cancellationToken).ConfigureAwait(false);
        var pinnedStreams = received.Select(list => (IReadOnlyList<string>)list.Select(message => message.Record.Stream).Distinct().ToList()).ToList();
        await output.WriteLineAsync($"5. Appended two events to each of four streams; the first consumer received {Describe(pinnedStreams[0])} and the second {Describe(pinnedStreams[1])}: every stream with one consumer, in order.").ConfigureAwait(false);

        var info = await client.GetPersistentSubscriptionInfoAsync(Category, Group, cancellationToken).ConfigureAwait(false);
        var live = info.Live ?? throw new InvalidOperationException("The server did not say how the running group stands.");
        await output.WriteLineAsync($"6. Asked about the group: {live.ConsumerCount} consumers, {live.InFlightCount} in flight, each holding up to {string.Join(" and ", live.Consumers.Select(consumer => consumer.BufferSize))}.").ConfigureAwait(false);

        var updated = await client.UpdatePersistentSubscriptionAsync(Category, Group, settings with { ConsumerStrategy = ConsumerStrategy.RoundRobin }, cancellationToken).ConfigureAwait(false);
        var endedWith = await Task.WhenAll(consuming).ConfigureAwait(false);
        await output.WriteLineAsync($"7. Changed the group to round robin: both consumers' calls ended with {string.Join(" and ", endedWith)}, to connect again under the new settings.").ConfigureAwait(false);

        // Under round robin a slow consumer, which holds the one event it may, and a quick one,
        // which acknowledges everything; the quick one takes what the slow one cannot.
        await using var slow = await client.SubscribeToPersistentSubscriptionAsync(Category, Group, bufferSize: 1, cancellationToken).ConfigureAwait(false);
        await using var quick = await client.SubscribeToPersistentSubscriptionAsync(Category, Group, bufferSize: 1, cancellationToken).ConfigureAwait(false);
        var quickReceived = new List<PersistentSubscriptionMessage.Recorded>();
        var consumingQuickly = ConsumeAsync(quick, quickReceived, cancellationToken);
        await client.AppendToStreamAsync("orders-5", StreamState.NoStream, [CreateEvent("order_placed"), CreateEvent("order_paid"), CreateEvent("order_shipped")], cancellationToken).ConfigureAwait(false);
        var held = await slow.Messages.OfType<PersistentSubscriptionMessage.Recorded>().FirstAsync(cancellationToken).ConfigureAwait(false);
        await UntilAsync(() => quickReceived.Count == 2, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"8. Connected a slow and a quick consumer, each holding one event, and appended three to orders-5: the slow one holds revision {held.Record.Revision} unacknowledged, and the quick one received and acknowledged revisions {string.Join(" and ", quickReceived.Select(message => message.Record.Revision))}.").ConfigureAwait(false);

        await slow.DisposeAsync().ConfigureAwait(false);
        await UntilAsync(() => quickReceived.Count == 3, cancellationToken).ConfigureAwait(false);
        var handedOver = quickReceived[2].Record.Id == held.Record.Id;
        await output.WriteLineAsync($"9. The slow consumer left: the quick one received revision {quickReceived[2].Record.Revision} at once, with retry count {quickReceived[2].RetryCount}; the event did nothing wrong.").ConfigureAwait(false);

        await quick.DisposeAsync().ConfigureAwait(false);
        await consumingQuickly.ConfigureAwait(false);
        await client.DeletePersistentSubscriptionAsync(Category, Group, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("10. The quick consumer left; deleted the group; dropping the database.").ConfigureAwait(false);

        return new Report(
            pinnedStreams,
            received.Sum(list => list.Count),
            info,
            endedWith,
            updated,
            quickReceived.Select(message => (message.Record.Revision, message.RetryCount)).ToList(),
            handedOver);
    }

    private static EventData CreateEvent(string type) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    private static string Describe(IReadOnlyList<string> streams) =>
        streams.Count == 0 ? "nothing" : string.Join(", ", streams);

    /// <summary>Consumes and acknowledges until the subscription ends, saying how: on its own, or because the group was updated.</summary>
    private static async Task<string> ConsumeAsync(Client.PersistentSubscription subscription, List<PersistentSubscriptionMessage.Recorded> received, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                received.Add(message);
                await subscription.AckAsync(message.Record.Id).ConfigureAwait(false);
            }

            return "the end of the subscription";
        }
        catch (GroupUpdatedException)
        {
            return nameof(GroupUpdatedException);
        }
    }

    /// <summary>Waits for what a consumer receives on its own task.</summary>
    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }
}
