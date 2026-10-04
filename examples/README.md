# examples

End-to-end sample programs. Each runs against a local SQL Server with nothing
but a connection string, and its test project runs the same scenarios, so
every sample is also an integration test. There is one component per backend,
holding every scenario of that backend, so the scenarios build once and run
from one program.

| Component | Purpose | Status |
|---|---|---|
| [`polecat`](polecat/README.md) | the scenarios of the Polecat-backed server: append and read, catch-up subscriptions, the virtual streams by position and by ordinal, redirects between two instances, delete and tombstone, persistent subscriptions | active |

Each component has its own solution and `README.md`. To add one, follow
`agentics/rules/coding/csharp/csharp-new-project.md` (or `/new` in Claude Code)
— it appends the row here. To add a scenario to a component, follow that
component's README.
