# client

What an application uses to talk to a Nightingale server.

| | |
|---|---|
| Category | `libraries/` |
| Kind | library |
| Solution | `Dracocephalum.Nightingale.Client.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Client`, `test/Dracocephalum.Nightingale.Client.Tests` |

## What it does

The client-side package: `NightingaleClient` over the generated stub, with
append, bounded reads of a plain stream, of `$all` and of the virtual streams
(`$ce-<category>`, `$et-<event type>`), and catch-up subscriptions to any of
them, delivered through a one-slot channel so a slow
consumer holds the server back; delete and tombstone, which a server refuses
unless its host allows them; persistent-subscription groups, created, consumed
with acknowledgements and refusals, replayed and deleted; the mapping from the wire's failure reasons
to the shared exceptions; and the connection options. It
carries Core and the client transport and nothing else - no server, no backend.

## Run it

    dotnet build Dracocephalum.Nightingale.Client.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Client.slnx

A library; nothing to run. Consumers reference the package.

## Configuration

None read from anywhere: the caller constructs the client with
`NightingaleClientOptions`, which names the server address, or with a gRPC
call invoker of its own.

## Notes

References Core as a project until a package feed exists; see TODO.md.
