# core

The abstractions and the wire contract every other component builds on.

| | |
|---|---|
| Category | `libraries/` |
| Kind | library |
| Solution | `Dracocephalum.Nightingale.Core.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Core`, `src/Dracocephalum.Nightingale.Protocols.Grpc`, `test/Dracocephalum.Nightingale.Core.Tests`, `test/Dracocephalum.Nightingale.Protocols.Grpc.Tests` |

## What it does

Two assemblies. `Dracocephalum.Nightingale.Core` holds the value types and
exceptions every other component shares — `StreamPosition`, `StreamState`,
`EventData`, `EventRecord`, `StreamHead`, `AppendResult`, the stream, group,
credential and tenant exceptions and `NightingaleJson`, the one set of
serializer options for JSON that passes through — and references no wire
package: the domain knows nothing of any transport, and the compiler holds it
to that. Its root namespace is `Dracocephalum.Nightingale`: the `.Core` suffix
names the assembly, not the namespace.

`Dracocephalum.Nightingale.Protocols.Grpc` is the gRPC contract: `V1/*.proto`,
from which `Grpc.Tools` generates the messages, the client stub and the service
base at build time into the `Dracocephalum.Nightingale.Protocols.Grpc.V1`
namespace, so the client and the server components agree by construction; and
beside them `WireConversions`, the mapping between those messages and the
domain types, and `ErrorReasons`, the failure reasons the contract carries. A
second stack — another IDL with its own RPC — would be a sibling,
`Protocols.<Stack>`, referencing `Core` the same way and never the other
stack.

## Run it

    dotnet build Dracocephalum.Nightingale.Core.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Core.slnx

Run from this folder. The repository root has no solution and fails `MSB1003` by
design. The generated gRPC code lands under `obj/` and is not committed.

## Configuration

None.

## Notes

The contract is split by area the way the reference protocol is: `V1/` holds
`streams.proto`, `server_features.proto`, `persistent_subscriptions.proto` and
`management.proto`, with `shared.proto` for the numbers, names, bodies, headers
and error conventions and `errors.proto` for the failure reasons. The proto
package, `dracocephalum.nightingale.v1`, is what goes on the wire and does not
change with the assembly's name. The port the server calls and a backend
implements, `IStreamStore`, lives in the server component beside the services
that call it; the domain types here are what crosses it and what the client
hands to callers. `TODO.md` says which parts of the contract are implemented.
