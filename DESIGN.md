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
| `client` | `NightingaleClient` over the generated stub, opened from one connection string in the reference client's shape that lists the instances and carries every setting, and the mapping from wire failure reasons to the shared exceptions | the server's internals |
| `server` | the service implementations, the port a backend implements, `IStreamStore`, and the tail every subscription waits on, `IStoreTail` | any particular store |
| `server` (Polecat backend) | `PolecatStreamStore`, the tailer over the store's high-water agent, the sequencer that assigns ordinals, the schema additions, the initializer that owns the database, and the default host | the wire |

The port is deliberately small: append with an expected state, read a bounded
slice. Everything the contract adds, paging, the head-first read order, error
mapping, lives in the service above it, once, for every backend.

## The seams

### 1. Events carry no CLR type

The store, like every framework of its kind, wants an event to be a .NET class
it can serialize and name. The gateway has no event classes: the client's
type name and JSON body are the whole event. `JsonEvent` is the seam. It is the
store's event wrapper over a `JsonElement`, sealed, with the type name the
client sent held in a property whose setter does nothing, so the store's own
naming pass cannot overwrite it. The body is written and read as JSON text
with `NightingaleJson.Default`, the one set of serializer options every
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
the events table, and on a store initialized with ordinals the two ordinal
columns and their filtered indexes (seam 6), and the gateway's own tables are
added as a feature beside it. `SingleTenancy` exists only to hand this database class
to the store, because the store's default tenancy always builds the plain one.

The virtual streams are read straight from the table, `VirtualStreamReader`,
because the store's query surface knows nothing of the added column and
reaches the type column only through its own type registry; the reader's SQL
is what the two indexes were declared for, and an integration test holds its
rows to the same bytes the store's own path returns.

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
rule: the server initializes only an empty database, and never changes a
store while serving it unless told to.

Two mechanisms keep the database's tables, and they are kept in one order.
The event store's tables, with the gateway's additions to them, are compared
and changed by the store's own schema tooling, which works from the
difference between the real tables and what this version of the store
expects: a comparison, not a version number, so a change the store's library
brings with an upgrade is found whether or not anyone expected it. The
gateway's own tables are created and changed by its migrations (seam 7),
whose history says which have run. The store's go first, because the
gateway's tables are about a store that exists. `StoreSchema` is the one
place that reports what is pending in both and applies it; startup, the
default host's switches and a host's own code all go through it.

- A database that does not exist is created, with the configured collation,
  when `Nightingale:CreateDatabase` allows it.
- An empty database, created by the server or provisioned by hand, gets the
  store's tables and then the gateway's while every table is empty, so no
  index is ever built over data, and a row per property of the store in
  `StoreProperty`: the settings below that shaped it, with the actual
  collation, then when and by which server version it was initialized, the
  version of the store's library and the hash of its creation script.
- An initialized database, one that has any of the gateway's migrations, is
  refused when it has a migration this server does not know, because the
  store is then newer than the server; refused when the configuration
  contradicts a setting it was initialized with; and refused when either set
  of tables is behind what this server expects, with what would be applied
  in the message, unless `Nightingale:ApplySchemaChanges` is on.
  The comparison of the store's tables is the slow part of a start the first
  time the database server compiles it, about ten seconds for the eighty
  catalog statements the store's schema library sends, and a fraction of a
  second after. Every apply records the hash of the store's creation script,
  which covers the store library, the schema library, the gateway's additions
  and the settings; with `Nightingale:FastBoot` on, a start that finds its own
  hash recorded skips the comparison. It trades away noticing a change made
  to the tables outside the server, so it is off by default and the report
  and the apply never use it. The integration tests that are not about
  initialization make their store from the creation script and start with
  fast boot on; one test holds a store made that way equal to one the server
  initialized, catalog and recorded rows.
- A database with tables and none of the gateway's migrations is refused
  outright: it is not ours.

The same report and the same apply are there without starting the server:
`--schema-report` on the default host prints what the database needs and
exits with 1 when it is not current, `--apply-schema` brings it up to date,
and a host of its own calls `GetNightingaleSchemaReportAsync` and
`ApplyNightingaleSchemaAsync`.

The settings under `Nightingale:Store` are the ones fixed at initialization
because each shapes the store's tables or where they live: the schema they
are in, `dbo` unless said otherwise; the collation, which must be a UTF-8 one so
that a name holds any character, and which decides whether names that differ
in case are the same name, binary and so case-sensitive unless said otherwise; the partitioning mode, one of none, by
tenant, which gives each tenant its own sequence, or by the archived flag,
which keeps deleted streams' events out of every live read, one mode rather
than one switch per scheme because a table has one partition scheme; and
whether ordinals are assigned (seam 6). Changing any of them means a new
database, and the store's property rows record each so a changed
configuration is refused rather than served.

The table is called `StoreProperty` because not every row is a setting: some
are what the store was initialized with, `Partitioning`, `Collation`, and the
rest are facts about it, `CreatedAt`, `StoreSchemaHash`. Every row is a name
and a value, one level: a setting is a single value, never an object or a
list, so its row's name is its member's name with no path in front, and a
test holds the settings type to that. The setting rows
are read by the configuration binder into `StoreSettings`, the base of the
type the configuration binds into. Both are seen through one interface,
`IStoreSettings`, which every backend's settings carry, and the comparison
is written against it, property by property, and the table never changes shape: a store older than
a setting has no row for it and reads as the default, and a row a newer
server wrote is ignored by an older one.

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

### 5. A group runs in one place, on a lease

A persistent-subscription group is a checkpoint the server keeps and a set of
events in flight to one consumer. The checkpoint, the group's settings and its
parked messages are rows in the gateway's own tables, through the `ISubscriptionGroupStore`
port and its one implementation, `SubscriptionGroupStore`, over the context of seam 7;
the in-flight events, their retry counts and their deadlines are in
memory, in `SubscriptionGroupRuntime`, in the one instance that holds the group's lease.
The lease is a row that only a free, expired or already owned lease lets an
instance write, two instances racing for it told apart by the row's
concurrency tokens, renewed while the consumer stays, released when it
leaves. The lease carries the owner's address, derived from what the owner
listens on unless configured, and an instance asked for a group another runs
refuses with that address; the client goes there itself, once, and keeps the
connection, the way the reference client follows a not-leader answer to the
leader. That is the whole of the cluster: no gossip, no registry, no relay,
the database the only coordination point. A replay is refused the same way
while a consumer is connected elsewhere, since only the instance running the
group can wake it. A relay inside the cluster, for a client that can reach
one load-balanced address only, would sit on the same lease-carried address
and is not built. The checkpoint is the last position every
delivered event up to which is done: acknowledged, skipped, or parked. Parked
messages are rows, one per event, so one can be replayed by itself; the
reference keeps them in a stream and can only replay them together. A parked
row carries every number its event has, the global position as its key, the
revision within its stream, and the ordinal when the group is numbered by
ordinal, so a replay addresses it by whichever number the group speaks. A
replay is a move, the shape of a service bus's dead-letter queue: the row
goes from parked to the group's outbox, a message is always in exactly one of
the two, and a delivery that fails again moves it back with its new reason
and count. Delivery has the reference's shape: one dispatcher owns the
consumer's slots and serves a list of retries before the live buffer, so
what is due on the outbox, queued as retries when the consumer connects and
whenever the group is woken, which the replay call does through the registry
when the consumer is connected to the same instance, goes out ahead of the
next event the stream would have sent; the events already in flight complete
as they are. A message stays on the outbox while it is in flight, so a wake
in the meantime never delivers it twice. The outbox row has a due time, so a
retry with a delay is the same row with a later one.

`IStoreTail` is the seam for liveness. One tailer per process runs the store's
own high-water agent, the part of its async daemon that finds that mark, and
publishes every advance; each subscription waits on it instead of polling the
store, so a thousand subscribers cost the store one poll per interval. A
subscription confirms with the head, catches up in pages, says so, then
delivers whatever each advance brings: a stream subscription re-reads its own
stream from where it left off, an `$all` subscription reads the advanced range.
Where now matters, a subscription from the end or a bounded read of `$all`,
the tail refreshes itself first with one query through the committed rows that
follow the mark without a gap, so an append that has returned is never on the
wrong side of "now". It never passes a gap; giving up an abandoned gap is the
poller's call, after the store's threshold.

### 6. Ordinals are assigned after commit, by one sequencer

A virtual stream's positions are sparse, so two of them subtracted are not a
count. The reference gets dense numbers from link events with their own
revisions; ours are two nullable columns on the events table, the category
ordinal and the type ordinal, on a store initialized with
`Nightingale:Store:AssignOrdinals`. Each has a filtered index over the numbered rows
only, leading with the tenant column, so a read by ordinal is one seek and can
never return an unnumbered row. The columns are declared through the same
patched schema as the category column and recorded in the marker row, because
they shape the table: a store initialized without them refuses the setting
until a backfill exists, and one initialized with them refuses to run without.

`OrdinalSequencer` is the sequencer: one per cluster, on the same lease rows the
persistent-subscription groups use, walking the events table behind the
high-water mark in batches and giving each event its place within its
category and its type, each batch continuing from the last ordinal of each
key found with one backward seek. The append path is untouched, because
numbering at append time would serialize every append to a category. A batch
and the progress row, the position numbered through, are one transaction, and
the progress row is read under an update lock, so a sequencer whose lease
overlapped a takeover continues from what the other committed. The price is
lag: ordinals trail the high-water mark by the sequencer's cadence, which is
the tail's, and a removed event leaves a numbered hole the reader skips and
the sequencer never reuses, which is what a link whose event is gone does in
the reference.

The numbering is the caller's explicit choice per read or subscription,
global by default, and the service refuses an ordinal read on anything but
`$ce-` and `$et-`, and with `ORDINALS_NOT_ENABLED` on a store without the
feature, before anything is delivered. Under ordinal numbering every number in
the conversation is an ordinal and each event carries its ordinal beside the
position it always carries; a consumer keeps the numbering with its checkpoint
and never resumes under the other. A bounded read is a snapshot of what is
numbered and needs no head, because a batch is atomic. A subscription waits
on the tail like every other, then looks for newly numbered rows; while the
sequencer is behind the head it woke for, it looks again on a short interval
rather than waiting for an advance a quiet store would never bring. A
persistent-subscription group chooses its numbering once, when it is created,
and keeps it for life: its start, its checkpoint and the key of a parked
message are all in that numbering, so a consumer resuming the group can never
mix the two, and one that wants the other numbering creates another group.
Ordinals are per tenant by construction, the sequencer partitions by tenant and
the indexes lead with the tenant column, so a read across all tenants has no
single ordinal sequence and is served under global numbering only.

### 7. The gateway's own tables are one model, and its own schema

A group's settings are columns of its row, a value each: its start, its
timeouts in milliseconds, its bounds, and its numbering by name. A group is
one of many rows of one shape, which is what columns are for; they are read
and compared in the database, and changing one is a column update. The
store's properties are the other case, one object with a few facts, and are
a row each.

A group's row has three kinds of column: its settings, its state, the
checkpoint and when it was created, and a snapshot of how it stands while
it runs, the columns named `Live`, written with each renewal of its lease
and cleared when its consumer leaves. The snapshot is what lets any
instance list every group with its live numbers. It is read only under a
held lease and nothing decides by it.

The tables the gateway keeps, subscription groups, their parked events and
outbox, leases, the sequencer's progress and the store's properties, are one EF
Core context, `NightingaleDbContext`, in the server library. The context owns
them, under the repository's EF rules for a context that does: a `Guid`
key named `Id` on each, singular table names, natural keys as unique
indexes, a check that fails a test when a line is forgotten. A column that
refers to a row of another table is named for that table, `SubscriptionGroupId`;
when the table is the gateway's own the column is a foreign key to it, never
cascading, and the check holds the name and the key to each other. When the
table is the event store's, the column keeps the name and takes no
constraint: the store's tables are the store's, an event's id has no unique
index to refer to there, and the store deletes events of a tombstoned stream
whatever refers to them. Such a column is listed by name where the check is
run, and anything that joins to the store's events joins on the position,
which is their key. It names no
provider and no column type, only lengths and whether a string is Unicode,
so each backend generates its own migrations from the one model, with
`dotnet ef migrations add`, into its own project: SQL Server's are in the
Polecat backend, and the Marten backend brings PostgreSQL's for the same
context, not a second model. A second test fails when the model has changed
and no migration carries the change.

The tables live in a schema of their own, `nightingale` unless
`Nightingale:Schema` names another, with the migrations history beside
them, so the gateway's tables and the event store's never share a
mechanism or a history; the schema may be the store's own, the names do
not collide. Generated migrations carry the schema as a literal, and a host
may name another, so the operations are moved to the host's schema as they
run, by the backend's migrations SQL generator; the generated code is never
edited. A model is cached per context type with its schema in it, so the
schema is part of the cache key: one process serving two schemas would
otherwise read the first one's tables through the second context.

A group's key is an id made with the group, and its tenant, stream and
group name sit under a unique index: the names find a group, by whatever
rule the database's collation has for which names are the same, and the id
is the group. Its parked events, its outbox and its lease go by the id, so
nothing downstream of the lookup depends on how a name is spelt or compared.
A park that arrives for a group deleted meanwhile is refused by the foreign
key and taken for what it is: the group is gone.

The backend has a second, read-only context beside it, a mirror of two
tables the store owns: its events, with the columns the gateway adds, and
the row its high-water mark is kept in. The reads by category, by event
type and by position are LINQ over that mirror, and so is the tail's look
for the unbroken run of positions after the mark, whose gap rule is plain
code. A query names the columns it needs, never a whole row, because the
ordinal columns exist only on a store initialized with ordinals; and the
mirror declares the store's column types exactly, since a name sent as
Unicode to a column that is not makes the database convert the column and
scan the index it should seek. One test holds every mapped column to the
store's own definition, another asks the database for its count of seeks
and scans on the index. The mirror refuses to save, and exists as two
types over one mapping, `EventsDbContext` on the main connection and
`ReadOnlyEventsDbContext` on the read-only one (seam 8), each with a
registration of its own, so a reader asks for the connection it means by
type.

What stays SQL text, because its shape is the point or there is no table
to map: the sequencer's batch, which numbers thousands of events in one
set-based statement under a lock where row-by-row writes would be an order
of magnitude slower; and the initializer's questions of the catalog and
its creation of the database.

The group store's plain reads and writes, the reader's queries and the
store's property rows run on the in-memory provider in unit tests. That provider
has no transactions, no unique indexes, no collation and no filtered
indexes, so it stands in for exactly those reads and writes and for nothing
else; the lease is written so that it needs nothing else, an optimistic
write settled by the row's concurrency tokens, and the integration suite
still runs the same tests on SQL Server.

### 8. History is read from a read-only connection

The backend opens a second connection, read-only, and by default it is the
main connection string with a read-only application intent: a listener with
read-only routing sends it to a readable secondary, and any other server
ignores the intent, so a standalone server pays a second connection pool and
nothing else. The store has no read and write split of its own; this one is
the backend's.

A secondary applies the log after the primary commits, under synchronous
commit too, so it holds a prefix of the events. Reading it bounded by the
primary's high-water mark would skip, silently and inside a subscription,
whatever it has not applied yet. The mark is replicated with the events, and
it is written after the events it covers, so everything at or below the mark
*as the secondary reports it* is on that secondary. A page of `$all` or of a
virtual stream by position is therefore asked of the read-only connection
together with its mark, in one round trip so both come from the same server:
the part of the page at or below that mark comes back, and the main
connection supplies whatever lies above. The page is the one the main
connection alone would have returned. A reader working through history is
served by the secondary; a reader at the head is not sent there at all, a
position above a mark seen within the last second going straight to the main
connection; a secondary that fails is left alone for ten seconds.

What a deletion changes is the one thing that can differ: an event archived
on the primary a moment ago may still be read from the secondary, as it
would have been by a reader a moment earlier.

Bounded reads of a plain stream stay on the main connection, because a
client reads what it has just appended and appends on what it read. A host
may move them, `Nightingale:ReadStreamsFromReadOnlyConnection`: they are
then eventually consistent, nothing lost or reordered, and the cost is a
revision conflict where the read was behind, since an append's expected
revision is always checked on the main connection. Subscriptions, heads,
ordinal reads, every write, the leases and the sequencer use the main
connection whatever is configured.

## Conventions the code relies on

- **Expected states** are the reference client's: any, no stream, stream
  exists, or an exact revision. The store's expected version is the version
  after the append, one-based, and the conversion happens once, in
  `PolecatStreamStore`.
- **Idempotent retries** follow the reference rule: on a conflict with an
  explicit expectation, the stored events from that revision on are compared
  with the batch by id, and an exact match returns the original result. Event
  ids are not enforced unique.
- **Names** come in two vocabularies, on purpose. The contract, the wire and
  the client say "persistent subscription" and "group", as the reference
  does, so they read as the contract their callers already know:
  `GroupSettings`, `GroupExistsException`, `GROUP_NOT_FOUND`. The server's
  own types and tables say "subscription group" for the same thing:
  `SubscriptionGroupStore`, `SubscriptionParkedEvent`. The difference stops
  at the wire, and each contract type says so where it is declared.
- **Data access** lives in a `Data` folder and namespace: the contexts, their
  entities, and the migrations under `Data/Migrations`. A context exposes
  every table it maps through a set named for its entity, the class name or
  its plural, which the conventions check holds.
- **Errors** cross the wire as a `google.rpc.Status` with an `ErrorInfo`
  detail whose reason is one of `errors.proto`'s; the client maps reasons to
  the shared exceptions, and unknown reasons stay `RpcException`.
- **Options** are one tree bound from the `Nightingale` section: the server
  library's `NightingaleOptionsBase` holds what every backend shares, such as
  which deletions the host allows, a backend's options derive from it and add
  their own, and the registration accepts any type that derives from the base.
- **Tenancy** is conjoined from day one, with the default tenant, so enabling
  tenants later is a matter of credentials and a header, not a migration.

## How it is tested

- `*.Tests` projects are unit tests and need nothing running: the service
  against a strict fake of the port and a tail a test moves by hand, the
  client against a scripted call, the conversions, the marker comparison, the
  group store on the in-memory provider, and the model against the schema
  feature.
- `*.Tests.Integration` projects need the local SQL Server and run only with
  `-p:RunIntegrationTests=true`: the store adapter, the initializer's every
  path, and the two guards above. Each creates its databases and drops them.
- `examples/` holds one program per backend that narrates each of its scenarios
  end to end, and a test per scenario that asserts the same run, against a
  database the server creates and the scenario drops.
