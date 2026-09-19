# libraries

Packages published to NuGet.

| Component | Purpose | Status |
|---|---|---|
| [`core`](core/README.md) | the abstractions and the wire contract every other component builds on | active |
| [`client`](client/README.md) | what an application uses to talk to a Nightingale server | active |
| [`server`](server/README.md) | the gRPC service implementations a host mounts, and the Polecat backend with the default host | active |

Each component has its own solution and `README.md`. To add one, follow
`agentics/rules/coding/csharp/csharp-new-project.md` (or `/new` in Claude Code)
— it appends the row here.
