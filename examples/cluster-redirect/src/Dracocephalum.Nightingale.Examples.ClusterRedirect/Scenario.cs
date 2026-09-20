using System.Text;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Common;

namespace Dracocephalum.Nightingale.Examples.ClusterRedirect;

/// <summary>
/// Two server instances over one store, step by step: a persistent-subscription group runs in
/// the instance its consumer connected to, and a client that asks the other instance for that
/// group is sent to the right one and goes there on its own, the way the reference client
/// follows a not-leader answer. A second consumer asked of the wrong instance ends up refused
/// by the right one, and a replay asked of the wrong instance reaches the consumer at once. The
/// program prints the steps; the test project asserts the report. Both run the same code, so the
/// sample can never drift from what is verified.
/// </summary>
public static class Scenario
{
    private const string Stream = "orders-1";
    private const string Group = "billing";

    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Delivered">The types the consumer received before the replay.</param>
    /// <param name="SecondConsumerRefusal">The name of the exception the second consumer got, asked of the other instance.</param>
    /// <param name="Replayed">How many messages the replay, asked of the other instance, put back.</param>
    /// <param name="ReplayedType">The type of the event the consumer received after the replay.</param>
    public sealed record Report(
        IReadOnlyList<string> Delivered,
        string SecondConsumerRefusal,
        int Replayed,
        string ReplayedType);

    /// <summary>Runs the scenario.</summary>
    /// <param name="masterConnectionString">A connection string to the server's master database.</param>
    /// <param name="output">Where the steps are narrated.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public static async Task<Report> RunAsync(string masterConnectionString, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        await using var database = ExampleDatabase.Reserve(masterConnectionString);
        await output.WriteLineAsync($"1. Reserved the database name {database.Name}; the first server creates it.").ConfigureAwait(false);

        await using var first = await ExampleServer.StartAsync(database.ConnectionString, cancellationToken).ConfigureAwait(false);
        await using var second = await ExampleServer.StartAsync(database.ConnectionString, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"2. Two instances over one store: {first.Address} and {second.Address}, each advertising the address it listens on.").ConfigureAwait(false);

        await using var toFirst = first.Connect();
        await using var toSecond = second.Connect();

        await toSecond.Client.AppendToStreamAsync(Stream, StreamState.NoStream, [Event("order_placed"), Event("order_paid")], cancellationToken).ConfigureAwait(false);
        await toSecond.Client.CreatePersistentSubscriptionAsync(Stream, Group, GroupSettings.Default with { Start = StreamPosition.Start }, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("3. Through the second instance: appended two events and created the group, which any instance serves.").ConfigureAwait(false);

        var delivered = new List<string>();
        await using var consumer = await toFirst.Client.SubscribeToPersistentSubscriptionAsync(Stream, Group, bufferSize: 5, cancellationToken).ConfigureAwait(false);
        var messages = consumer.GetAsyncEnumerator(cancellationToken);
        await messages.MoveNextAsync().ConfigureAwait(false);
        var placed = messages.Current.Record;
        delivered.Add(placed.Type);
        await messages.MoveNextAsync().ConfigureAwait(false);
        var paid = messages.Current.Record;
        delivered.Add(paid.Type);
        await consumer.NackAsync(NackAction.Park, "payment gateway down", placed.Id).ConfigureAwait(false);
        await consumer.AckAsync(paid.Id).ConfigureAwait(false);
        await output.WriteLineAsync($"4. A consumer connected to the first instance, which now runs the group; it received {string.Join(", ", delivered)}, parked the first and acknowledged the second.").ConfigureAwait(false);

        string refusal;
        try
        {
            await using var intruder = await toSecond.Client.SubscribeToPersistentSubscriptionAsync(Stream, Group, cancellationToken: cancellationToken).ConfigureAwait(false);
            refusal = "nothing";
        }
        catch (ConsumerLimitReachedException)
        {
            refusal = nameof(ConsumerLimitReachedException);
        }

        await output.WriteLineAsync($"5. A second consumer asked the second instance, was sent to the first, and was refused there: {refusal}.").ConfigureAwait(false);

        var replayed = await ReplayWhenParkedAsync(toSecond.Client, cancellationToken).ConfigureAwait(false);
        await messages.MoveNextAsync().ConfigureAwait(false);
        var replayedType = messages.Current.Record.Type;
        await consumer.AckAsync(messages.Current.Record.Id).ConfigureAwait(false);
        await output.WriteLineAsync($"6. Replayed the parked message through the second instance: sent to the first, {replayed} put back, and the consumer received the {replayedType} at once.").ConfigureAwait(false);

        await messages.DisposeAsync().ConfigureAwait(false);
        await output.WriteLineAsync("7. Disposed the consumer; dropping the database.").ConfigureAwait(false);

        return new Report(delivered, refusal, replayed, replayedType);
    }

    private static EventData Event(string type) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    /// <summary>
    /// Replays the parked message. A refusal is one-way, so the park may not have landed when the
    /// replay is asked for; it is asked again shortly, as an operator's tool would.
    /// </summary>
    private static async Task<int> ReplayWhenParkedAsync(NightingaleClient client, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await client.ReplayParkedMessagesAsync(Stream, Group, position: 0, cancellationToken).ConfigureAwait(false);
            }
            catch (ParkedMessageNotFoundException) when (attempt < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
