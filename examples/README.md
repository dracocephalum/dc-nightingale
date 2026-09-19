# examples

End-to-end sample programs. Each runs against a local SQL Server with nothing
but a connection string, and its test project runs the same scenario, so every
sample is also an integration test.

| Component | Purpose | Status |
|---|---|---|
| [`append-and-read`](append-and-read/README.md) | the client round trip against an in-process server: append, read both ways, conflict, idempotent retry | active |

Each component has its own solution and `README.md`. To add one, follow
`agentics/rules/coding/csharp/csharp-new-project.md` (or `/new` in Claude Code)
— it appends the row here.
