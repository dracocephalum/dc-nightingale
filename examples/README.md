# examples

End-to-end sample programs. Each runs against a local SQL Server with nothing
but a connection string, and its test project runs the same scenario, so every
sample is also an integration test.

| Component | Purpose | Status |
|---|---|---|
| [`append-and-read`](append-and-read/README.md) | the client round trip against an in-process server: append, read both ways, conflict, idempotent retry | active |
| [`catch-up-subscription`](catch-up-subscription/README.md) | two catch-up subscriptions, to a stream from its start and to `$all` from its end: catch up, caught-up note, live delivery in order | active |

Each component has its own solution and `README.md`. To add one, follow
`agentics/rules/coding/csharp/csharp-new-project.md` (or `/new` in Claude Code)
— it appends the row here.
