using System.Text;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;

namespace Dracocephalum.Nightingale.Examples.Polecat.StreamListings;

/// <summary>
/// The virtual streams over a workflow and over the streams themselves, against a Nightingale
/// server, step by step: <c>$bc-</c>, every event of one correlation across the streams it
/// touched; <c>$streams</c> and <c>$category-</c>, the first event of every stream and of a
/// category's; a stream named just its category, which is in the category; and the exact filters
/// on <c>$all</c>, a whole category and a type, against the prefix. The program prints the steps;
/// the test project asserts the report.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Workflow">Stream and type of every event of the workflow's correlation, in position order.</param>
    /// <param name="Streams">The streams <c>$streams</c> listed, in the order they began.</param>
    /// <param name="OrdersStreams">The streams <c>$category-orders</c> listed.</param>
    /// <param name="OrdersCategory">The streams of the events <c>$ce-orders</c> held, in position order.</param>
    /// <param name="ByCategoryFilter">The streams of the events <c>$all</c> kept under the category filter <c>orders</c>.</param>
    /// <param name="ByPrefixFilter">The streams of the events <c>$all</c> kept under the prefix filter <c>orders-</c>.</param>
    /// <param name="ByTypeFilter">The types of the events <c>$all</c> kept under the type filter <c>order_placed</c>.</param>
    /// <param name="LiveListed">The stream a subscription to <c>$streams</c> was handed once a new one began.</param>
    public sealed record Report(
        IReadOnlyList<string> Workflow,
        IReadOnlyList<string> Streams,
        IReadOnlyList<string> OrdersStreams,
        IReadOnlyList<string> OrdersCategory,
        IReadOnlyList<string> ByCategoryFilter,
        IReadOnlyList<string> ByPrefixFilter,
        IReadOnlyList<string> ByTypeFilter,
        string LiveListed);

    /// <summary>Runs the scenario.</summary>
    /// <param name="run">The run the scenario is part of: its database, its server, and a tenant for the scenario.</param>
    /// <param name="output">Where the steps are narrated.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public static async Task<Report> RunAsync(ExampleRun run, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(output);

        var tenant = await run.CreateTenantAsync("stream-listings", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"1. Created the tenant {tenant.Name} for this scenario; its streams and its virtual streams live there and nowhere else.").ConfigureAwait(false);

        await using var connection = run.Server.Connect(tenant);
        var client = connection.Client;

        await client.AppendToStreamAsync("orders-1", StreamState.NoStream, [CreateEvent("order_placed", "checkout-1"), CreateEvent("order_placed_v2")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("payments-1", StreamState.NoStream, [CreateEvent("payment_taken", "checkout-1")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("orders", StreamState.NoStream, [CreateEvent("orders_counted")], cancellationToken).ConfigureAwait(false);
        var appended = await client.AppendToStreamAsync("orders-1", StreamState.Any, [CreateEvent("order_shipped", "checkout-1")], cancellationToken).ConfigureAwait(false);
        await ExampleMark.WaitUntilCoversAsync(client, appended.Position, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("2. Appended to orders-1, payments-1, a stream named just orders, and orders-1 again; three of the events carry the correlation id checkout-1.").ConfigureAwait(false);

        var workflow = await ReadAsync(client, "$bc-checkout-1", record => record.Stream + ":" + record.Type, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"3. Read $bc-checkout-1, the workflow across the streams it touched: {string.Join(", ", workflow)}.").ConfigureAwait(false);

        var streams = await ReadAsync(client, "$streams", record => record.Stream, cancellationToken).ConfigureAwait(false);
        var ordersStreams = await ReadAsync(client, "$category-orders", record => record.Stream, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"4. Read $streams, one event a stream, its first: {string.Join(", ", streams)}; and $category-orders: {string.Join(", ", ordersStreams)}.").ConfigureAwait(false);

        var ordersCategory = await ReadAsync(client, "$ce-orders", record => record.Stream, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"5. Read $ce-orders: {string.Join(", ", ordersCategory)}. A name without a hyphen is its own category, so the stream named orders is in it.").ConfigureAwait(false);

        var byCategory = await ReadAllAsync(client, StreamFilter.Category("orders"), record => record.Stream, cancellationToken).ConfigureAwait(false);
        var byPrefix = await ReadAllAsync(client, StreamFilter.Prefix("orders-"), record => record.Stream, cancellationToken).ConfigureAwait(false);
        var byType = await ReadAllAsync(client, EventTypeFilter.Type("order_placed"), record => record.Type, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"6. Read $all three ways: the category orders keeps {string.Join(", ", byCategory)}; the prefix orders- keeps {string.Join(", ", byPrefix)}, since orders does not begin with it; the type order_placed keeps {string.Join(", ", byType)}, not order_placed_v2.").ConfigureAwait(false);

        await using var subscription = client.SubscribeToStreamAsync("$streams", StreamPosition.End, cancellationToken: cancellationToken);
        var messages = subscription.Messages.GetAsyncEnumerator(cancellationToken);
        await subscription.Confirmed.ConfigureAwait(false);
        await client.AppendToStreamAsync("payments-1", StreamState.Any, [CreateEvent("payment_refunded")], cancellationToken).ConfigureAwait(false);
        await client.AppendToStreamAsync("refunds-1", StreamState.NoStream, [CreateEvent("refund_issued")], cancellationToken).ConfigureAwait(false);
        var liveListed = string.Empty;
        while (await messages.MoveNextAsync().ConfigureAwait(false))
        {
            if (messages.Current is SubscriptionMessage.Recorded recorded)
            {
                liveListed = recorded.Record.Stream;
                break;
            }
        }

        await messages.DisposeAsync().ConfigureAwait(false);
        await output.WriteLineAsync($"7. Subscribed to $streams from the end, then moved payments-1 on and began refunds-1: the subscription was handed {liveListed}, and nothing for payments-1, which had begun already.").ConfigureAwait(false);

        return new Report(workflow, streams, ordersStreams, ordersCategory, byCategory, byPrefix, byType, liveListed);
    }

    private static async Task<IReadOnlyList<string>> ReadAsync(NightingaleClient client, string stream, Func<EventRecord, string> describe, CancellationToken cancellationToken)
    {
        var described = new List<string>();
        await using var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken);
        await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            described.Add(describe(record));
        }

        return described;
    }

    private static async Task<IReadOnlyList<string>> ReadAllAsync(NightingaleClient client, EventFilter filter, Func<EventRecord, string> describe, CancellationToken cancellationToken)
    {
        var described = new List<string>();
        await using var read = client.ReadAllAsync(Direction.Forwards, StreamPosition.Start, filter: filter, cancellationToken: cancellationToken);
        await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            described.Add(describe(record));
        }

        return described;
    }

    private static EventData CreateEvent(string type, string? correlationId = null) =>
        new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"), correlationId is null ? null : new JsonObject { [MetadataKeys.CorrelationId] = correlationId });
}
