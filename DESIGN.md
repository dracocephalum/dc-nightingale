# Design

How the pieces fit and why the store is wrapped the way it is. Read this before
changing anything under `libraries/`; the type comments say what each class
does, this page says why the classes exist at all. What is deliberately
different from the reference client is in [`VARIANCES.md`](VARIANCES.md), what
is deferred with its approach decided is in [`PENDING.md`](PENDING.md), and
what is owed soon is in [`TODO.md`](TODO.md).

## What it is

Nightingale is a gRPC event-store gateway. Applications use a client shaped
like the reference event store's client: append to a stream with an expected
revision, read a stream or `$all` in either direction, subscribe. The server
stores the events in a document and event-sourcing framework on SQL Server
(Polecat), and later on PostgreSQL (Marten), without the applications knowing
which. The contract is Nightingale's own, written in the reference client's
shape because that shape is familiar and proven; the reference's own client
is not meant to connect to this server, and nothing is owed to its wire
format, only borrowed from it where useful. The sizing target is the deployment it replaces: three nodes, about
twenty terabytes, about a hundred services connected over gRPC.

## The shape

    application ──▶ NightingaleClient ──▶ gRPC (Protocol.V1) ──▶ StreamsService ──▶ IStreamStore ──▶ PolecatStreamStore ──▶ Polecat ──▶ SQL Server
                    libraries/client       libraries/core          libraries/server                    libraries/server (Polecat backend)

| Component | Holds | Knows nothing about |
|---|---|---|
| `core` | the wire contract (`Protocol/*.proto`) and the domain types both sides share: positions, expected states, event data and records, exceptions | any store, any transport detail |
| `client` | `NightingaleClient` over the generated stub, and the mapping from wire failure reasons to the shared exceptions | the server's internals |
| `server` | the service implementations and the port a backend implements, `IStreamStore` | any particular store |
| `server` (Polecat backend) | `PolecatStreamStore`, the schema additions, the initializer that owns the database, and the default host | the wire |

The port is deliberately small: append with an expected state, read a bounded
slice. Everything the contract adds, paging, the head-first read order, error
mapping, lives in the service above it, once, for every backend.

## The four seams

### 1. Events carry no CLR type

The store, like every framework of its kind, wants an event to be a .NET class
it can serialize and name. The gateway has no event classes: the client's
type name and JSON body are the whole event. `JsonEvent` is the seam. It is the
store's event wrapper over a `JsonElement`, sealed, with the type name the
client sent held in a property whose setter does nothing, so the store's own
naming pass cannot overwrite it. The body is written and read as JSON text
with `NightingaleJson.Options`, the one set of serializer options every
component uses for JSON it does not own, so the client's casing, key order,
number formatting and unicode come back byte for byte, insignificant
whitespace aside.

Metadata is a JSON object, also sent as bytes. Two keys the server
understands, `$correlationId` and `$causationId`, move into the store's own
columns on the way in and are appended to the object on the way out, so they
come last. Everything else stays in the headers column as the client sent it.

An integration test guards this seam: if a store release stops honouring the
caller's type name, or changes what a body comes back as, it fails.

### 2. The schema is patched, not replaced

Category and event-type streams (`$ce-orders`, `$et-OrderCreated`) are
predicates over the events table, not link streams. For them to be seeks
rather than scans the table needs a persisted computed `category` column and
two filtered indexes. The store's migration drops any column or index it does
not know about, so they cannot be applied by hand; they have to be part of
the definition the store migrates from.

`AugmentedPolecatDatabase` is that seam. It is the store's database class with one
override: when the store builds its feature schemas, the event-store feature
is wrapped in `PatchedEventStoreFeature`, which adds the column and indexes to
the events table, and the gateway's own `nightingale_store` table is added as
a feature beside it. `SingleTenancy` exists only to hand this database class
to the store, because the store's default tenancy always builds the plain one.

The store's automatic schema management is off. Its table ensurer rebuilds
the event-store definition from the options on first use, bypassing the
tenancy's database, and would drop the additions. The initializer applies
the schema instead, through the patched database. A second integration test
guards this: apply the schema twice and the second delta is empty.

The store's own `Database` property keeps the plain definition; it cannot be
replaced without forking. Two things read it. Tenant partition provisioning
only splits partitions through it, which the tenant-partitioning test shows
leaves the augmented schema without a delta. The store's schema export would
write the plain schema, so the export is the gateway's: the default host's
`--export-schema <file>` writes the script the server would apply, built
from the augmented definition without connecting to anything.

### 3. The server owns the database

A host never configures the store; it names a database. The initializer,
`StoreInitializer`, runs at startup before any request is served, under one
rule: the server initializes only an empty database, and never migrates a
store while serving it.

- A database that does not exist is created, with the configured collation,
  when `Nightingale:CreateDatabase` allows it.
- An empty database, created by the server or provisioned by hand, gets the
  whole schema in one pass while every table is empty, so no index is ever
  built over data, and one row in `nightingale_store` recording what shaped
  it: the schema version, the partitioning choices, the actual collation,
  when and by which server version.
- A database with that row is compared with the configuration and refused on
  any difference, then its schema is asserted to match what this server
  expects. A difference in schema is refused too, with the change it would
  apply in the message, unless `Nightingale:ApplySchemaChanges` is on.
- A database with tables and no row is refused outright: it is not ours.

The settings under `Nightingale:Store` are the ones fixed at initialization
because each shapes the events table: the collation, which decides whether
stream names are case-sensitive; and the partitioning mode, one of none, by
tenant, which gives each tenant its own sequence, or by the archived flag,
which keeps deleted streams' events out of every live read. It is one mode
rather than one switch per scheme because a table has one partition scheme.
Changing either setting means a new database.

### 4. Positions are the store's sequence

A stream revision is the store's version minus one, so revisions start at
zero as the reference client expects. A position is the store's global
sequence number, one signed 64-bit value. `$all` and the virtual streams are
predicates over it, so their positions are sparse: monotonic, but with other
streams' events between. The head of a virtual stream is its own last event,
never the global head.

Sequence numbers are assigned before a transaction commits, so a forward read
of `$all` or a subscription must stop at the store's high-water mark, which
advances by polling; per-stream reads are unaffected because versions are
dense under the stream lock. Under tenant partitioning there is no global
sequence, each tenant has its own, so the wildcard read across tenants is
refused there and everything else still works.

## Conventions the code relies on

- **Expected states** are the reference client's: any, no stream, stream
  exists, or an exact revision. The store's expected version is the version
  after the append, one-based, and the conversion happens once, in
  `PolecatStreamStore`.
- **Idempotent retries** follow the reference rule: on a conflict with an
  explicit expectation, the stored events from that revision on are compared
  with the batch by id, and an exact match returns the original result. Event
  ids are not enforced unique.
- **Errors** cross the wire as a `google.rpc.Status` with an `ErrorInfo`
  detail whose reason is one of `errors.proto`'s; the client maps reasons to
  the shared exceptions, and unknown reasons stay `RpcException`.
- **Tenancy** is conjoined from day one, with the default tenant, so enabling
  tenants later is a matter of credentials and a header, not a migration.

## How it is tested

- `*.Tests` projects are unit tests and need nothing running: the service
  against a strict fake of the port, the client against an in-process fake
  server, the conversions, the marker comparison.
- `*.Tests.Integration` projects need the local SQL Server and run only with
  `-p:RunIntegrationTests=true`: the store adapter, the initializer's every
  path, and the two guards above. Each creates its databases and drops them.
- `examples/` are programs that narrate a scenario end to end and tests that
  assert the same run, against a database the server creates and the sample
  drops.
