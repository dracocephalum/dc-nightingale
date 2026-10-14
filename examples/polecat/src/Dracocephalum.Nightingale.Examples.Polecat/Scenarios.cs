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
            "binary-data",
            "an event's second body: encrypted personal data beside readable JSON, forgotten by destroying its key",
            BinaryData.Scenario.RunAsync,
            report => $"Done: {report.Stream} read back with binary data beside its JSON; the key destroyed, its JSON still says {report.CountryAfterShredding}."),
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
            "credentials-and-tenants",
            "credentials and tenants: roles kept to, a password changed, a tenant made and disabled",
            CredentialsAndTenants.Scenario.RunAsync,
            report => $"Done: {report.Made.Count} credentials made; the user was refused as {report.ReaderRefusal}; tenant {report.Tenant.Name} disabled, its credential refused as {report.DisabledRefusal}."),
        ExampleScenario.Create(
            "persistent-subscription",
            "a group: consume with acknowledgements, retry and park, replay, resume from the checkpoint",
            PersistentSubscription.Scenario.RunAsync,
            report => $"Done: {report.Delivered.Count} delivered, one parked and replayed as {report.ReplayedType}; the group ended at checkpoint {report.CheckpointOnReturn}."),
        ExampleScenario.Create(
            "reset-archive-delete",
            "reset hides a stream until its next append, archive hides it everywhere, delete frees its name; the default server only resets",
            ResetArchiveDelete.Scenario.RunAsync,
            report => $"Done: reset, recreated at revision {report.RecreatedRevision}, archived, deleted and reused {report.Stream}; the default server refused to archive with {report.DefaultServerRefusal}."),
        ExampleScenario.Create(
            "stream-listings",
            "a workflow by correlation, the streams and a category's by their first events, and the exact filters on $all",
            StreamListings.Scenario.RunAsync,
            report => $"Done: the workflow touched {report.Workflow.Count} events; $streams listed {report.Streams.Count} streams and then {report.LiveListed} live."),
        ExampleScenario.Create(
            "stream-metadata",
            "a stream's metadata: set before the stream, read back, raced, its history, $acl refused, $all, $de and $mde",
            StreamMetadata.Scenario.RunAsync,
            report => $"Done: metadata of {report.Stream} set twice and read back at revision {report.Final.Revision}; $all {report.All}, $de {report.DataEvents}, $mde {report.MetadataEvents}."),
    ];
}
