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
| Honour a caller-supplied event type name on a prebuilt event | the gateway's event type is carried through its own sticky `Event<JsonElement>` seam (`DESIGN.md`) | not yet |
| Expose Weasel's ignored-index list or an add-index call for the events table, or a first-class category column | the patched schema, which adds the category and type columns and their indexes beside the store's (`DESIGN.md`) | not yet |
| The event-store table ensurer should respect the tenancy database | the initializer runs the ensurer itself, over the one database, before the gateway's own migrations | not yet |
| Guard the high-water mark's update so a lagging detector cannot lower it | nothing yet; several gateway instances each run the store's agent and each persists the mark, and a slow one can write an older value | not yet |
| Write an append in as many commands as it needs inside its one transaction | the append limit (`VARIANCES.md`), since one command stops at the database's 2100 parameters, 190 events; paging on the server in `PENDING.md` waits on this | not yet |
| A conditional archive that carries the expected revision | the delete's revision check, which reads, compares and archives in one session without being atomic (`TODO.md`) | not yet |
| Expose the per-tenant high-water detection: make the store's detector public, or let the daemon poll a tenant set the caller supplies | `PolecatTenantTails`, the gateway's own per-tenant poller under `Nightingale:Store:Partitioning` `Tenant` (`DESIGN.md`, seam 4), which reads the marks itself and keeps the store's rule for giving an abandoned gap up | not yet |
| Cancel the high-water agent's wait when it is stopped: its stop clears a flag and awaits the loop, which is parked in the wake-up's wait for the whole slow polling time | `AppendSignal.Stop`, which the tail calls before stopping the daemon so that the agent's wait ends at once; without it a stop lasted the polling interval, five minutes in a test that set it so | not yet |
| An online, and resumable, option on the schema library's SQL Server index definition, as its PostgreSQL one has a concurrent option, so an index over a large events table is built without blocking it | nothing yet: the server runs the statements the library writes (`DESIGN.md`, seam 3), and will append the option to the index statements itself, where the edition has it, until the library writes it (`PENDING.md`, *Online and resumable index builds*) | not yet |
