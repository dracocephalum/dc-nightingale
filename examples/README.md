# examples

End-to-end sample programs. Each runs against a local SQL Server with nothing
but a connection string, and its test project runs the same scenario, so every
sample is also an integration test. They share one library, `common`, so a
sample is only its scenario.

| Component | Purpose | Status |
|---|---|---|
| [`common`](common/README.md) | what every sample shares, not a sample: the throwaway database, the in-process server, the client over it, the program shell | active |
| [`append-and-read`](append-and-read/README.md) | the client round trip against an in-process server: append, read both ways, conflict, idempotent retry | active |
| [`catch-up-subscription`](catch-up-subscription/README.md) | two catch-up subscriptions, to a stream from its start and to `$all` from its end: catch up, caught-up note, live delivery in order | active |
| [`category-stream`](category-stream/README.md) | the virtual streams: read `$ce-orders` and `$et-order_placed` across plain streams, subscribe to the category and see only its events | active |
| [`category-ordinals`](category-ordinals/README.md) | the virtual streams by ordinal, on a store initialized with ordinals: dense numbers whose bounds are a count, a subscription confirmed at an ordinal, and the holes a delete leaves | active |
| [`cluster-redirect`](cluster-redirect/README.md) | two instances over one store: a group runs where its consumer connected, the other instance refuses with that address, and the client goes there for a second consumer and for a replay | active |
| [`delete-and-tombstone`](delete-and-tombstone/README.md) | delete hides a stream, tombstone removes it and frees the name, a stale revision is refused, and a server with the default settings refuses to delete at all | active |
| [`persistent-subscription`](persistent-subscription/README.md) | a persistent-subscription group: create, consume with acknowledgements, retry and park, replay the parked message one at a time, resume from the checkpoint, delete | active |

Each component has its own solution and `README.md`. To add one, follow
`agentics/rules/coding/csharp/csharp-new-project.md` (or `/new` in Claude Code)
— it appends the row here.
