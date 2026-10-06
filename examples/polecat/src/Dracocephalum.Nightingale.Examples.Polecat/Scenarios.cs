using Dracocephalum.Nightingale.Examples.Polecat.Common;

namespace Dracocephalum.Nightingale.Examples.Polecat;

/// <summary>
/// The scenarios there are, each in a folder of its own beside this file with a README that
/// walks through it. A new scenario is a folder, a line here and a test.
/// </summary>
public static class Scenarios
{
    /// <summary>Gets every scenario, in the order <c>all</c> runs them.</summary>
    public static IReadOnlyList<ExampleScenario> All { get; } =
    [
        ExampleScenario.Create(
            "append-and-read",
            "the client round trip: append, read both ways, conflict, idempotent retry",
            AppendAndRead.Scenario.RunAsync,
            report => $"Done: {report.ReadForwards.Count} events round-tripped on {report.Stream}."),
        ExampleScenario.Create(
            "catch-up-subscription",
            "subscribe to a stream from its start and to $all from its end: catch up, then live",
            CatchUpSubscription.Scenario.RunAsync,
            report => $"Done: {report.CaughtUp.Count} events caught up and {report.Live.Count + report.AllLive.Count} delivered live."),
        ExampleScenario.Create(
            "category-stream",
            "the virtual streams: read $ce-orders and $et-order_placed, subscribe to the category",
            CategoryStream.Scenario.RunAsync,
            report => $"Done: {report.Category.Count} category events and {report.ByType.Count} typed events read; {report.Live.Count} delivered live."),
        ExampleScenario.Create(
            "category-ordinals",
            "the virtual streams by ordinal: dense numbers, and the holes a delete leaves",
            CategoryOrdinals.Scenario.RunAsync,
            report => $"Done: ordinals {string.Join(", ", report.Ordinals)} read, {string.Join(", ", report.AfterDelete)} after the delete; live ordinal {report.LiveOrdinal}; the group's checkpoint {report.GroupCheckpoint}."),
        ExampleScenario.Create(
            "cluster-redirect",
            "two instances over one store: a group runs on one, the other sends the client there",
            ClusterRedirect.Scenario.RunAsync,
            report => $"Done: the second consumer was refused as {report.SecondConsumerRefusal}; {report.Replayed} replayed through the other instance and the {report.ReplayedType} arrived at once."),
        ExampleScenario.Create(
            "competing-consumers",
            "two consumers on one group: pinned streams, both ended by an update, a leaver's event handed over",
            CompetingConsumers.Scenario.RunAsync,
            report => $"Done: {report.PinnedDelivered} events pinned over {report.PinnedStreams[0].Count} and {report.PinnedStreams[1].Count} streams; both ended with {report.EndedWith[0]}; the leaver's event was handed over with retry count {report.QuickReceived[^1].RetryCount}."),
        ExampleScenario.Create(
            "delete-and-tombstone",
            "delete hides a stream, tombstone frees its name, and the default server refuses both",
            DeleteAndTombstone.Scenario.RunAsync,
            report => $"Done: deleted, tombstoned and revived {report.Stream}; the default server refused with {report.DefaultServerRefusal}."),
        ExampleScenario.Create(
            "persistent-subscription",
            "a group: consume with acknowledgements, retry and park, replay, resume from the checkpoint",
            PersistentSubscription.Scenario.RunAsync,
            report => $"Done: {report.Delivered.Count} delivered, one parked and replayed as {report.ReplayedType}; the group ended at checkpoint {report.CheckpointOnReturn}."),
    ];
}
