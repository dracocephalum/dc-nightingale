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
to the shared exceptions; and the connection string, which holds every
setting a client has. It
carries Core and the client transport and nothing else - no server, no backend.

## Run it

    dotnet build Dracocephalum.Nightingale.Client.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Client.slnx

A library; nothing to run. Consumers reference the package.

## Configuration

One connection string, in the shape of the reference client's, kept wherever
the application keeps its other connection strings; the client reads no
configuration section and no environment variable. `new NightingaleClient(connectionString)`
parses it, and `NightingaleClientSettings.Parse` gives the settings to a caller
that wants to look first. A caller with a gRPC call invoker of its own, such
as a test server's, passes that instead.

    nightingale://[user:password@]host[:port][,host[:port]...][?key=value&...]
    nightingale+discover://[user:password@]host[:port][?key=value&...]

Several hosts are the instances of one cluster: every instance serves every
call, so they are used in rotation and one that is down is passed over. The
`+discover` form names one host whose DNS record lists the instances. A host
without a port gets 2113, the reference's.

| Key | Default | Meaning |
|---|---|---|
| `tls` | `true` | Encrypt the connection. `false` speaks cleartext HTTP/2, and such a connection never goes through an HTTP proxy, which cannot carry it. |
| `tlsVerifyCert` | `true` | Verify the server's certificate. `false` is for a development certificate only. |
| `keepAliveInterval` | `10000` | Milliseconds a connection may be idle before it is pinged; `-1` never pings. |
| `keepAliveTimeout` | `10000` | Milliseconds a ping may go unanswered before the connection is closed; `-1` sets no limit. |
| `defaultDeadline` | none | Milliseconds given to a call that is not a read or a subscription and carries no deadline of its own. |

Keys are case-insensitive. A key the client does not know, or one given
twice, is refused with a message that names it and never repeats the
credentials. Credentials are read and held; nothing is sent until the server
authenticates, see `PENDING.md`.

## Notes

References Core as a project until a package feed exists; see TODO.md.
