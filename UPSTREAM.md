# Upstream requests

What this repository would ask of the store's maintainers, each with the
limit it works around meanwhile. These are not owed: the gateway never
modifies the store's source, and a request here waits until one is filed and
answered, at a lower priority than anything in `TODO.md`. A request that
blocks a slice is a `TODO.md` entry for as long as it blocks, pointing here.
When a request is filed, its link goes in the row; when it is answered, the
workaround is removed and the row with it.

| Request | What it would replace | Filed |
|---|---|---|
| Hydrate the headers column in the raw-events query, as the stream fetch does: of the store's event hydration paths, the one behind `QueryAllRawEvents()` reads the correlation, causation and user name columns and not the headers, so an event read through it loses the metadata it was appended with | every page of `$all` is read through the gateway's own mirror of the events table, which reads the headers, whichever connection serves it (`DESIGN.md`, seam 8) | not yet |
| Let an event keep its JSON body beside a binary one: the binary path writes the `{}` placeholder over the JSON, and the readers take a non-null binary column as the only body, so a row cannot hold both. A per-event choice, or a serializer that may answer "no binary data for this one", would also let an event type be binary only sometimes | `BinaryDataWriter`, the transaction participant that sets the binary column after the store's own insert, one more statement in the append's transaction (`DESIGN.md`, seam 1); and the gateway never reading events through the store's own readers, which refuse such a row | not yet |
| Bind a queued command's parameters by the value's type: `QueueSqlCommand` makes every parameter text, so a byte array is refused | nothing needed: the transaction participant runs a command of the gateway's own, with typed parameters | not yet |
| Assert a stream's version under a lock: the check of a stream that a session does not append to (`AlwaysEnforceConsistency` with an expected version and no events) reads the stream's row without one, so an append to it committing between the read and the session's commit passes the check | `StreamStateChecker`, the transaction participant that reads the checked streams' rows under an update lock held to the commit (`DESIGN.md`, seam 1) | not yet |
| Honour a caller-supplied event type name on a prebuilt event | the gateway's event type is carried through its own sticky `Event<JsonElement>` seam (`DESIGN.md`) | not yet |
| A first-class category column on the events table | the patched schema, which adds the category and type columns beside the store's (`DESIGN.md`). The indexes the request once covered need nothing more: the schema library honours an ignored index on SQL Server from 9.27, which is the seam *Online and resumable index builds* in `PENDING.md` takes them out through | not yet |
| The event-store table ensurer should respect the tenancy database | the initializer runs the ensurer itself, over the one database, before the gateway's own migrations | not yet |
| Guard the high-water mark's update so a lagging detector cannot lower it | nothing yet; several gateway instances each run the store's agent and each persists the mark, and a slow one can write an older value | not yet |
| Write an append in as many commands as it needs inside its one transaction | the append limit (`VARIANCES.md`), since one command stops at the database's 2100 parameters, 190 events; paging on the server in `PENDING.md` waits on this | not yet |
| A conditional archive that carries the expected revision | the archive's revision check, which reads, compares and archives in one session without being atomic (`TODO.md`) | not yet |
| Expose the per-tenant high-water detection: make the store's detector public, or let the daemon poll a tenant set the caller supplies | `PolecatTenantTails`, the gateway's own per-tenant poller under `Nightingale:Store:Partitioning` `Tenant` (`DESIGN.md`, seam 4), which reads the marks itself and keeps the store's rule for giving an abandoned gap up | not yet |
| Cancel the high-water agent's wait when it is stopped: its stop clears a flag and awaits the loop, which is parked in the wake-up's wait for the whole slow polling time | `AppendSignal.Stop`, which the tail calls before stopping the daemon so that the agent's wait ends at once; without it a stop lasted the polling interval, five minutes in a test that set it so | not yet |
| An online, and resumable, option on the schema library's SQL Server index definition, as its PostgreSQL one has a concurrent option, so an index over a large events table is built without blocking it | nothing yet, and not needed by the gateway once it applies the events table's indexes itself (`PENDING.md`, *Online and resumable index builds*); still worth asking, for every other user of the library on SQL Server | not yet |
