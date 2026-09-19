# core

The abstractions and the wire contract every other component builds on.

| | |
|---|---|
| Category | `libraries/` |
| Kind | library |
| Solution | `Dracocephalum.Nightingale.Core.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Core`, `test/Dracocephalum.Nightingale.Core.Tests` |

## What it does

The value types and exceptions every other component shares — `StreamPosition`,
`StreamState`, `EventData`, `EventRecord`, `StreamHead`, `AppendResult` and
the three stream exceptions — and the wire contract: `Protocol/*.proto`,
from which `Grpc.Tools` generates the messages, the client stub and the service
base at build time, so the client and the server components agree by
construction. Its root namespace is `Dracocephalum.Nightingale`: the `.Core`
suffix names the assembly, not the namespace.

## Run it

    dotnet build Dracocephalum.Nightingale.Core.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Core.slnx

Run from this folder. The repository root has no solution and fails `MSB1003` by
design. The generated gRPC code lands under `obj/` and is not committed.

## Configuration

None.

## Notes

The contract is split by area the way the reference protocol is: `Protocol/`
holds `streams.proto` (drafted in full), `server_features.proto` (`Ping`),
`persistent_subscriptions.proto` (still an empty service), with `shared.proto`
for the numbers, names, bodies, headers and error conventions and
`errors.proto` for the failure reasons. The port the server calls and a
backend implements, `IStreamStore`, lives in the server component beside the
services that call it; the domain types here are what crosses it and what the
client hands to callers. `TODO.md` says which parts of the contract are
implemented.
