# server

The gRPC service implementations a host mounts, and the Polecat backend with
the default host.

| | |
|---|---|
| Category | `libraries/` |
| Kind | library, plus a service library |
| Solution | `Dracocephalum.Nightingale.Server.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Server`, `src/Dracocephalum.Nightingale.Server.Polecat`, `test/Dracocephalum.Nightingale.Server.Tests`, `test/Dracocephalum.Nightingale.Server.Polecat.Tests`, `test/Dracocephalum.Nightingale.Server.Polecat.Tests.Integration` |

## What it does

`Dracocephalum.Nightingale.Server` holds the service implementations of the
wire contract, the two ports a backend implements, `IStreamStore` for reads
and appends and `IStoreTail` for the head every subscription waits on, and
the two extension methods a host calls: `AddNightingaleServer()` to register and
`MapNightingaleServer()` to map. It has no entry point and no backend of its
own.

`Dracocephalum.Nightingale.Server.Polecat` is the backend over the Polecat
event-sourcing framework and the default host: one project that packs as a
library another host mounts through `AddNightingalePolecat()`, and runs on its
own with `dotnet run`. A backend belongs beside the server rather than in a
component of its own because it implements the server's port and ships with
every change to it; a later backend lands here the same way. The backend owns
its database: a host names one, and the server creates or initializes it,
records the settings that shaped it, and refuses a database that is not its
own. `DESIGN.md` at the repository root says why.

## Run it

    dotnet build Dracocephalum.Nightingale.Server.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Server.slnx

That runs the unit tests only. The backend's integration tests run against a
local SQL Server and live in the `*.Tests.Integration` project, which a plain
`dotnet test` never runs; ask for them explicitly:

    dotnet test Dracocephalum.Nightingale.Server.slnx -p:RunIntegrationTests=true

From this folder, the default host: `dotnet run --project src/Dracocephalum.Nightingale.Server.Polecat`.
Three switches make the same host do one thing and exit instead of serving;
append them after `--`:

| Switch | Does |
|---|---|
| `--export-schema schema.sql` | writes the creation script of a new store: the event store's tables with the gateway's additions, then the gateway's own tables; connects to nothing |
| `--schema-report` | prints what the configured database needs: that it is not initialized yet, or changes to the event store's tables, the gateway's pending migrations, and settings the configuration contradicts; exit code 0 when it is current, 1 when it is not |
| `--apply-schema` | brings the configured database up to date, the event store's tables first and the gateway's after, whatever `Nightingale:ApplySchemaChanges` says |

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
| `Nightingale:CreateDatabase` | `true` | create the database when it does not exist; off, a missing database is an error, for a host whose login may not create one |
| `Nightingale:ApplySchemaChanges` | `false` | apply what a store initialized earlier needs at startup: changes to the event store's tables, then the gateway's pending migrations; off, a database that is behind is refused with what would be applied in the message |
| `Nightingale:Schema` | `nightingale` | the schema the gateway keeps its own tables and its migrations history in; it may be the event store's schema |
| `Nightingale:UseReadOnlyConnection` | `true` | open a second, read-only connection and serve from it the pages of `$all` and of the virtual streams that lie at or below its own high-water mark; exact, and where there is no readable secondary it reaches the same server |
| `Nightingale:ReadOnlyConnectionStringName` | none | which entry under `ConnectionStrings` the read-only connection uses, exactly as written; unset, the main string with `ApplicationIntent=ReadOnly`, which a listener with read-only routing sends to a secondary |
| `Nightingale:ReadStreamsFromReadOnlyConnection` | `false` | serve bounded reads of plain streams from the read-only connection too; they are then eventually consistent, and an append made on a read that was behind is refused with a revision conflict |
| `Nightingale:Deletion:AllowDelete` | `false` | accept `Delete`: a stream's events leave every read and it cannot be appended to again; the rows stay |
| `Nightingale:Deletion:AllowTombstone` | `false` | accept `Tombstone`: a stream and its events are removed for good and the name is free again; irreversible, so a switch of its own |
| `Nightingale:Store:Schema` | `dbo` | the schema the event store's tables live in |
| `Nightingale:Store:Collation` | `Latin1_General_100_BIN2_UTF8` | the collation the database is created with, and the one a database provisioned by hand must have. It must be a UTF-8 collation: any other is refused, because names in another script would collide. The default is binary, so names that differ in case are different names; a case-insensitive UTF-8 collation makes them one |
| `Nightingale:Store:IgnoreCollationCompatibility` | `false` | work with a collation that is not UTF-8, for a database that already has one or a server older than SQL Server 2019. The host's own choice and consequence: a character of a name outside the collation's code page is stored as a question mark, so two such names can become one. Not recorded with the store; the server logs a warning at every start |
| `Nightingale:Store:Partitioning` | `None` | `Tenant` partitions the events table by tenant, each with its own sequence, and the wildcard tenant is then refused; `ArchivedStream` partitions it by the archived flag, so deleted streams' events sit apart; one mode, because a table has one partition scheme |

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
table; the type comments say why each is the way it is. Migration DDL is not
logged. References Core as a project until a package feed exists; see TODO.md.
