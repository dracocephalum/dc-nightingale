# server

The gRPC service implementations a host mounts, and the Polecat backend with
the default host.

| | |
|---|---|
| Category | `libraries/` |
| Kind | library, plus a service library |
| Solution | `Dracocephalum.Nightingale.Server.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Server`, `src/Dracocephalum.Nightingale.Server.Polecat`, `test/Dracocephalum.Nightingale.Server.Tests`, `test/Dracocephalum.Nightingale.Server.Polecat.Tests`, `test/Dracocephalum.Nightingale.Server.Polecat.Tests.Integration`, `test/Dracocephalum.Nightingale.Server.Contracts` |

## What it does

`Dracocephalum.Nightingale.Server` holds the application core — the two
ports a backend implements, `IStreamStore` for reads and appends and
`IStoreTail` for the head every subscription waits on, the subscription
runtime, and authorization and the credential and tenant managers, none of
which knows a transport — and, in its `Grpc` namespace, the gRPC transport
over it: the service implementations of the wire contract, the interceptor
that authenticates every call, and the mapping from the shared exceptions
to the statuses the contract promises. A host calls two extension methods:
`AddNightingaleServer()` to register the core and the transport, and
`MapNightingaleServer()` to map. It has no entry point and no backend of its
own.

Its `Sequencing` namespace holds what every backend supplies or shares for
its sequences: `SequenceTails`, the one poller of a process that keeps a
sequence head per sequence and the gateway's `SequenceHead` rows; the ports a
backend implements, the transaction horizon over its database and the reads
of its events table, which together are its head strategy; the checkpoint
the tails judge a gap by; and the ordinal sequencer, its lease, loop and
follower, registered by `AddNightingaleOrdinalSequencer` over the one port a
backend implements for it, `IOrdinalSequencer`, a batch in its own statement.
`test/Dracocephalum.Nightingale.Server.Contracts` holds those ports'
contracts as abstract tests, a library and not a test project: a backend's
integration project derives a class from each and supplies its database,
and the contract's tests run there.

`Dracocephalum.Nightingale.Server.Polecat` is the backend over the Polecat
event-sourcing framework and the default host: one project that packs as a
library another host mounts through `AddNightingalePolecat()`, and runs on its
own with `dotnet run`. A backend belongs beside the server rather than in a
component of its own because it implements the server's port and ships with
every change to it; a later backend lands here the same way. The backend owns
its database: a host names one; the server verifies it at every start and,
when `Nightingale:SchemaMode` allows, creates or initializes it and records
the settings that shaped it; a database that is not its own is refused.
`DESIGN.md` at the repository root says why.

## Database requirements

What a store needs, by what the host uses. A tier adds to the ones above it;
what the server can check, it checks at start-up and refuses with what is
missing. The server owns its database, as the reference event store owns its
storage: a transaction left open there after it has written anything, in
any table, holds the sequence heads at a gap until it ends, under the
transaction horizon, and the server names it in a warning after half a
minute. It stalls rather than skip an event that may still commit. The PostgreSQL column is the planned Marten backend's
(`PENDING.md`, *Marten backend*).

| Tier | SQL Server (Polecat) | PostgreSQL (Marten, planned) |
|---|---|---|
| **Core**: appends, reads and subscriptions, groups, metadata, appends to several streams, the virtual streams by position | SQL Server 2025 or later, for its native `json` type; a UTF-8 collation, `Latin1_General_100_BIN2_UTF8` by default; any edition, Express for development only. `VIEW DATABASE STATE` for the gateway's login is recommended, not required: with it the sequence heads are kept by the transaction horizon, which never skips an append that commits late and gives a rolled-back one up at once; without it by the best-effort strategy, a 3-second wait over reads that wait on an open append's row, chosen at start with a warning (`DESIGN.md`, seam 4) | PostgreSQL 13 or later, Marten's own floor |
| **Ordinals**: the virtual streams by number | nothing more: the sequencer numbers up to the sequence head, so ordinals are as exact as the head | nothing more: the transaction horizon reads what every role may (`PENDING.md`, *The PostgreSQL horizon*) |
| **Read replicas** | an availability group with a readable secondary, an Enterprise feature, or Azure | a streaming replica |
| **Partitioning** by tenant or by archived stream | table partitioning, in every edition since 2016 SP1 | declarative partitioning |
| **Online index builds** (`PENDING.md`) | Enterprise, or Azure | `CREATE INDEX CONCURRENTLY` |

## Run it

    dotnet build Dracocephalum.Nightingale.Server.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Server.slnx

That runs the unit tests only. The backend's integration tests run against a
local SQL Server and live in the `*.Tests.Integration` project, which a plain
`dotnet test` never runs; ask for them explicitly:

    dotnet test Dracocephalum.Nightingale.Server.slnx -p:RunIntegrationTests=true

From this folder, the default host: `dotnet run --project src/Dracocephalum.Nightingale.Server.Polecat`.
Four switches make the same host do one thing and exit instead of serving;
append them after `--`. The two that change the database do what the two
modes of `Nightingale:SchemaMode` do at startup, whatever the setting says,
so a pipeline runs them once and the servers start in the default mode:

| Switch | Does |
|---|---|
| `--export-schema schema.sql` | writes the creation script of a new store: the event store's tables with the gateway's additions, then the gateway's own tables; connects to nothing |
| `--schema-report` | prints what the configured database needs: that it is not initialized yet, or changes to the event store's tables, the gateway's pending migrations, and settings the configuration contradicts; exit code 0 when it is current, 1 when it is not |
| `--apply-schema` | applies what an initialized database is behind on, the event store's tables first and the gateway's after, as `Migrate` would; a database that is missing or not initialized is refused |
| `--initialize` | creates the database when it is missing, initializes it when it is empty, finishes an initialization that died half-way, and applies what it is behind on, as `Initialize` would; idempotent |

The gateway's own tables are created by EF Core migrations, generated from
the model in the server library into the backend that owns the provider.
After changing the model, from the backend's project folder:

    dotnet ef migrations add <Name> --context NightingaleDbContext --output-dir Data/Migrations

No database is needed for that, and the generated files are not edited; a
unit test fails while the model has a change no migration carries.

Until the first official release there is one migration, `Initial`, and it is
regenerated rather than added to: delete the `Data/Migrations` folder and run the
command with `Initial` as the name. No store from before the first release is
migrated; it is initialized again. From the first release on `Initial` is
frozen and every change is a migration of its own.

## Configuration

The server library reads nothing; configuration belongs to the host.

The default host binds the `Nightingale` section, one tree whose common part
is the server library's `NightingaleOptionsBase` and whose rest is the
backend's, and the connection string it names. `appsettings.json` carries the defaults; override any of them the usual
way, for example with the environment variable `ConnectionStrings__Nightingale`.

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings:<name>` | a local server, trusted login, database `nightingale` | the SQL Server connection string; it must name a database other than `master` |
| `Nightingale:ConnectionStringName` | `Nightingale` | which entry under `ConnectionStrings` the server uses |
| `Nightingale:SchemaMode` | `Verify` | what a start may do to the database. `Verify` checks it and refuses whatever is not what this server expects, missing, not initialized, behind, newer or initialized with other settings, naming the mode or switch that would fix it; the database is expected to be ready, as a service expects its migrations to have run elsewhere. `Migrate` also applies what an initialized store is behind on. `Initialize` also creates a missing database and initializes an empty one, and is idempotent. Every mode takes the schema lock, shared to verify and exclusive to change, so instances starting together do the work once |
| `Nightingale:IndexMigrationStatementTimeout` | `01:00:00` | how long a batch of the schema that creates or alters an index may run before the start stops it and fails, naming the batch: long enough for an index over a large events table, never unbounded, so a batch waiting on a lock another session holds does not hang silently. Every other batch runs under the connection's own command timeout. A change too large for this is one to run by hand; `--schema-report` prints it |
| `Nightingale:SchemaLockWaitTimeout` | `00:00:30` | how long a batch of the schema may wait for a lock another session holds on a table before it gives the wait up, to be tried again after as long, six times before the start fails naming the batch. Bounded for the system being served: a statement queued for a table's schema lock behind a long reader has every later reader and writer queue behind it, so the table is frozen for as long as it waits, and the wait between tries lets that queue drain. Every batch of both halves runs under it |
| `Nightingale:FastBoot` | `false` | at startup, take the event store's tables as current when the hash recorded with them is the hash of the creation script this server would apply, instead of comparing every table; the hash covers the store library, the schema library under it, the gateway's additions and the settings, so an upgrade still compares, but a change made to the tables outside the server goes unnoticed; the switches always compare |
| `Nightingale:TailPollingInterval` | `00:00:01` | how long the tail rests between looks at the store once it has caught up, which is how far behind an append made through another instance a subscriber on this one can be; an append made through this instance wakes its own tail at once. One small query per look per instance, whatever the number of subscriptions, so `00:00:00.250` buys a quarter of a second across instances for four queries a second. While a look moved the head and left it short of what is already committed, the tail looks again after a quarter of a second, or after this interval when it is shorter |
| `Nightingale:MaxEventsPerAppend` | `150` | the most events one append may carry; one more is refused with `APPEND_SIZE_EXCEEDED` before anything is written, and the caller sends several appends. At most 190, which is what the store takes in one write on SQL Server; more is refused at startup |
| `Nightingale:Schema` | `nightingale` | the schema the gateway keeps its own tables and its migrations history in; it may be the event store's schema |
| `Nightingale:UseReadOnlyConnection` | `true` | open a second, read-only connection and serve from it the pages of `$all` and of the virtual streams that lie at or below the sequence head it holds; exact, and where there is no readable secondary it reaches the same server |
| `Nightingale:ReadOnlyConnectionStringName` | none | which entry under `ConnectionStrings` the read-only connection uses, exactly as written; unset, the main string with `ApplicationIntent=ReadOnly`, which a listener with read-only routing sends to a secondary |
| `Nightingale:ReadStreamsFromReadOnlyConnection` | `false` | serve bounded reads of plain streams from the read-only connection too; they are then eventually consistent, and an append made on a read that was behind is refused with a revision conflict |
| `Nightingale:Deletion:Allowed` | `None` | how far a stream may be taken away: `None` refuses `Archive` and `Delete`, `Archive` allows archiving, a stream's events leaving every read and its appends refused with the rows kept, and `Delete` allows that and deleting, a stream and its events removed for good and the name free again. `Reset`, the reference client's delete, is allowed at every level: it writes one metadata event and removes nothing. A value that is not one of the three refuses to start |
| `Nightingale:Auth:Enabled` | `true` | authenticate every call but the features call from the `authorization` header; off, every caller is the administrator, for a private network or a test |
| `Nightingale:Auth:AdminPassword` | none | the built-in administrator's password; required while authentication is on and the administrator is not disabled, so that nothing done through the API can lock everyone out |
| `Nightingale:Auth:AdminDisabled` | `false` | refuse the built-in administrator, once other administrators exist |
| `Nightingale:Auth:Pepper` | none | a secret mixed into every password before it is hashed, so a copy of the credentials table verifies nothing without it; a key vault's configuration provider supplies it like any setting |
| `Nightingale:Auth:PepperId` | `1` | the pepper's id, kept with each hash; raise it with a new pepper and hashes are remade on the next login |
| `Nightingale:Auth:Iterations` | `600000` | key-derivation iterations for a new hash; a hash made with fewer is remade on the next login |
| `Nightingale:Auth:AllowInsecureTransport` | `false` | accept credentials over a connection that is not TLS, which sends the password as it is; for a private network |
| `Nightingale:Auth:VerificationCacheDuration` | `00:05:00` | how long a verified name and password are remembered on the instance |
| `Nightingale:Auth:LockoutThreshold` | `5` | wrong passwords in a row that lock a credential out; 0 for never |
| `Nightingale:Auth:LockoutDuration` | `00:15:00` | how long a locked-out credential is refused |
| `Nightingale:Auth:MinimumPasswordLength` | `12` | the shortest password accepted when one is set |
| `Nightingale:Tenants:RefreshInterval` | `00:00:10` | how often an instance reads the tenants again, which is how a tenant disabled elsewhere is noticed |
| `Nightingale:Store:Schema` | `dbo` | the schema the event store's tables live in |
| `Nightingale:Store:Collation` | `Latin1_General_100_BIN2_UTF8` | the collation the database is created with, and the one a database provisioned by hand must have. It must be a UTF-8 collation: any other is refused, because names in another script would collide. The default is binary, so names that differ in case are different names; a case-insensitive UTF-8 collation makes them one, on reads exactly and on live delivery as near as the server can match in process (`VARIANCES.md`, *Names are compared by the database*) |
| `Nightingale:Store:IgnoreCollationCompatibility` | `false` | work with a collation that is not UTF-8, for a database that already has one or a server older than SQL Server 2019. The host's own choice and consequence: a character of a name outside the collation's code page is stored as a question mark, so two such names can become one. Not recorded with the store; the server logs a warning at every start |
| `Nightingale:Store:Partitioning` | `None` | `Tenant` partitions the events table by tenant, each with its own sequence and its own sequence head, which the server keeps for every tenant it serves, and the wildcard tenant is then refused; `ArchivedStream` partitions it by the archived flag, so deleted streams' events sit apart; one mode, because a table has one partition scheme |

The settings under `Nightingale:Store` are fixed when the store is
initialized and checked on every later start: the server refuses to start
against a store whose record disagrees with them. Changing one means a new
database.

The integration tests create their databases on the server named by the
environment variable `NIGHTINGALE_SQLSERVER`, a `master` connection string;
unset, the same local default applies. Every database they create is named
`nightingale_test_` plus a random suffix and dropped when the test ends.

## Notes

The store is configured once, in `AddNightingalePolecat`: string stream
identity, conjoined tenancy with the default tenant, the partitioning the
settings ask for, the metadata columns the contract maps, and the store's own
schema management off. The initializer applies the schema through the
gateway's own database class, which declares a computed `category` column,
two filtered indexes on the events table, and the `nightingale_store` marker
table; the type comments say why each is the way it is. References Core as a
project until a package feed exists; see TODO.md.

What a start applies is logged, each half under its own category and at the
same levels. The gateway's migrations are logged by Entity Framework:
`Microsoft.EntityFrameworkCore.Migrations` says which migration, and
`Microsoft.EntityFrameworkCore.Database.Command` carries each statement, both
at `Information`. The event store's script is logged under
`Dracocephalum.Nightingale.Server.Polecat.StoreSchema`, one entry per table
or schema it changes at `Information`, and one that fails at `Error`. The
keys under `Logging:LogLevel` turn either on or off. Note that
`Database.Command` also carries every query the gateway runs, so a host that
lowers it to `Warning` for production silences the gateway's migration DDL
with it; the migration names and the store's script stay. Raise it for a
start that applies changes when the statements matter.
