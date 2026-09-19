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
wire contract, the port a backend implements (`IStreamStore`), and the two
extension methods a host calls: `AddNightingaleServer()` to register and
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
The same host writes the creation script it would apply, with the gateway's
additions, and exits: append `-- --export-schema schema.sql`. Nothing is
connected to for that.

## Configuration

The server library reads nothing; configuration belongs to the host.

The default host binds the `Nightingale` section and the connection string it
names. `appsettings.json` carries the defaults; override any of them the usual
way, for example with the environment variable `ConnectionStrings__Nightingale`.

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings:<name>` | a local server, trusted login, database `nightingale` | the SQL Server connection string; it must name a database other than `master` |
| `Nightingale:ConnectionStringName` | `Nightingale` | which entry under `ConnectionStrings` the server uses |
| `Nightingale:CreateDatabase` | `true` | create the database when it does not exist; off, a missing database is an error, for a host whose login may not create one |
| `Nightingale:ApplySchemaChanges` | `false` | apply pending schema changes at startup to a store initialized earlier; off, a schema that differs is refused with the change in the message |
| `Nightingale:Store:Collation` | none, the server's default | the collation the database is created with; a binary one such as `Latin1_General_100_BIN2` makes stream names case-sensitive |
| `Nightingale:Store:Partitioning` | `None` | `Tenant` partitions the events table by tenant, each with its own sequence, and the wildcard tenant is then refused; `ArchivedStream` partitions it by the archived flag, so deleted streams' events sit apart; one mode, because a table has one partition scheme |

The two settings under `Nightingale:Store` are fixed when the store is
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
