# TODO

Open items for this repository that are not a ticket yet: steps
initialization could not finish, decisions deferred, manual actions still
owed. Each entry says what would close it, so it can be picked up cold. When
an item becomes real work, open a ticket and remove it from here; when it is
done, remove it. An agent that leaves something unfinished, blocked, or
undecided adds it here rather than mentioning it once in a conversation.

## Initialization

- **Change tracking is `none`.** `.agentics.yaml` records no tracker, so
  branch names and pull-request titles carry no ticket. Close by: when the
  repository has a tracker, set `change-tracking.tracker: github-issues`.

## Decisions pending

- **Cross-component references are project references, not packages.**
  `agentics/rules/layout.md`, *Components reference each other as packages*,
  allows this as a recorded workaround while no package feed exists; each
  such reference carries the standard comment in its project file: the
  server's two projects and the client reference `core`, the sample
  program references the client and the server's backend. Close by: publish `core`,
  `client` and the server's two packages to a feed and replace every
  `ProjectReference` that crosses a component folder with a
  `PackageReference` at a version. Until then the coverage number of a
  component that references another is wrong: the referenced component's
  assembly is built from source and instrumented too, and its classes count
  at 0% (`server` reads 63% and `polecat` 71% with every class of their own
  at 100%). Read the per-class rates in the cobertura file for those two, not
  the total.
- **The gRPC contract is half drafted, and the first slice of it works end to
  end.** `libraries/core/src/.../Protocols.Grpc/V1/` splits the contract by area the
  way the reference protocol does. `streams.proto` is drafted in full, with
  `shared.proto` for the numbers, names, bodies, headers and error conventions
  and `errors.proto` for the reasons. Implemented and verified against a local
  SQL Server: `Append`, bounded reads and catch-up subscriptions over plain
  streams, `$all` and the virtual streams, `Reset`, and `Archive` and `Delete`
  behind the host's deletion level, persistent subscriptions with create, delete, one
  consumer with acknowledgements, retries, parking and replay, on a lease per
  group, and the virtual streams by ordinal on a store initialized with
  `Nightingale:Store:AssignOrdinals`, filters on `$all` with their
  checkpoints, for reads, catch-up subscriptions and persistent groups, and a
  stream's metadata, set and read back as events of `$$<stream>` and applied
  to reads of the stream, with `$de` and `$mde` beside `$all`; the samples
  under `examples/` are the end-to-end
  runs. Not yet: the ordinal
  backfill, in `PENDING.md`. Close by: see `PENDING.md`.
- **An archive's expected-revision check is not atomic with the archive.**
  The store reads the stream's revision, compares, then archives in the same
  session; an append that lands between the two is archived with the rest,
  so the outcome is still an archived stream, but the check passed on a
  revision that was no longer current. Close by: a conditional archive
  statement that carries the expected revision, which is asked for in
  `UPSTREAM.md`.
- **A virtual stream read across all tenants does not seek.** The indexes
  behind `$ce-` and `$et-` lead with the tenant, and a read under the
  wildcard names none, so it scans the index or the table with the category
  or type checked on every row: a full scan for a rare one. One tenant and a
  store of one tenant are fine, since the read names the tenant or the
  store's default one. Close by: the wildcard read becomes one seek per
  tenant of the directory, merged in position order in process, each asked
  for the next page's worth only, with an integration test over several
  tenants. Stores partitioned by tenant refuse the wildcard already.
- **An event can be appended that no client can read back.** Neither side
  sets gRPC's message limits, so both keep the defaults: the server receives
  at most 4 MB in a message and sends any size, and the client the same the
  other way round. An append sends one event a message and a read returns
  one event a message, so 4 MB is a limit on one event, not on an append. But
  a recorded event is larger than the proposed one, since it carries the
  stream name, revision, position, time and metadata the proposal does not,
  so an event just under the server's limit is stored and then refused by
  every client that reads it, catch-up and persistent subscriptions
  included, and the stream stalls on it. Binary data makes large events more
  likely. Close by: the first of a set of tunables, a size the server
  receives in one message, set on its gRPC options and checked at
  registration, and a connection-string setting for what the client
  receives, which by default exceeds the server's by the most a recorded
  event adds; a test appends an event at the server's limit and reads it
  back, by stream and by subscription.
