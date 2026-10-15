# Glossary

The words this repository uses for its own ideas, and the ones it keeps apart.
A term here means the same in code, comments and documents; a name that would
blur two of them is the wrong name. `DESIGN.md` says why each thing is shaped
the way it is.

## The hierarchy

```text
System
└─ Store                    1..n, any store provider, mixed
   ├─ Catalog               1: a database of its own, or the journal's role
   └─ Journal               1..n: the transaction horizon, connections, replicas
      └─ Store partition    1..n: one sequence; positions, sequence head, tail
         └─ Tenant          1..n: virtual streams and their ordinals
            └─ Stream       revisions
               └─ Event
```

Each level belongs to exactly one above it. Three rules follow:

- **Positions compare only within a store partition.** A read across tenants
  of several partitions has no single order of positions.
- **A tenant is placed in one store partition for its life.** Moving it gives
  every one of its events a new position, so subscribers' checkpoints break:
  a move is a migration with a reset, never a quiet rebalance.
- **State goes where its scope is.** What belongs to a store partition lives
  in its journal, beside the events, in the same transactions and the same
  replicas: the ordinal links, the sequencer's progress, the sequence head.
  What belongs to the store lives in its catalog: tenants, credentials.

How the tenancy modes fill the levels:

| Mode | Catalog | Journals | Store partitions in a journal | Tenants in a partition |
|---|---|---|---|---|
| Shared, the default | the journal | 1 | 1 | many |
| Partitioned by tenant | the journal | 1 | one per tenant | 1 |
| Database per tenant | its own | one per tenant | 1 | 1 |
| Sharded | its own | n | 1 | many |
| Sharded and partitioned by tenant | its own | n | one per tenant | 1 |

## Levels and roles

| Term | Means | Not |
|---|---|---|
| **System** | Nightingale as a whole: its stores, the servers and the clients. What it knows of its stores, their names and connection strings, is configuration for now. | |
| **Store** | One store provider over one catalog and one or more journals, in one tenancy mode. Its name is unique in the system, and an address is a store and a tenant: `store=abc;tenant=def`. | not the database: a store may span several |
| **Catalog** | Where a store keeps its tenants and where each is placed, and the gateway's state for the whole store. *Explicit*: a database of its own, as database per tenant needs. *Implicit*: read from the store's configuration, with the journal holding the gateway's tables. | not "master database", which on SQL Server is a system database |
| **Journal** | A database that holds events. The scope of a transaction horizon, of the gateway's connections and of a replica. | not a log |
| **Store partition** | The part of a journal with a sequence of its own: the one partition of a store not partitioned by tenant, or a tenant's own under partitioning by tenant. "Partition" alone where nothing else is meant. | not a table partition |
| **Table partition** | A physical partition of the events table. It lines up with a store partition under partitioning by tenant, and not under partitioning by archived stream or by range. | never a level of the hierarchy |
| **Tenant** | A name unique within its store, and the id the store provider keeps for it, `StoreTenantId`. | not unique across the system |
| **Replica** | A read-only copy of a journal. Its readers rely on the sequence head the primary keeps, since a replica cannot see the primary's open transactions. | |
| **Store provider** | An event-sourcing library the gateway runs on and leaves the heavy lifting to, the writes above all: Polecat on SQL Server, Marten on PostgreSQL (planned). It provides the event storage, its sessions and schema, and the high-water agent. | not the store, which is one provider's configured instance |
| **Client target** (protocol target) | A product whose clients the gateway means to serve through a protocol layer compatible with its own: KurrentDB. The older documents call it *the reference event store*, from when it was the only one. | not a store provider; the gateway never runs on it |
| **Gateway** | The server's role: the protocol, the subscriptions, and what it adds to a store provider. | |
| **Backend** | The gateway's implementation of its ports over one store provider, `Dracocephalum.Nightingale.Server.Polecat`. | |

## Numbers

| Term | Means | Not |
|---|---|---|
| **Sequence** | The series positions are drawn from, one per store partition: on Polecat the events table's identity column, or a tenant's sequence under partitioning by tenant; on Marten its events sequence. | not necessarily a database sequence object |
| **Position** | An event's number in its store partition's sequence, `seq_id`: rising, sparse, and unique within the partition. | not an offset in a log, as a client target's; not unique across partitions |
| **Revision** | An event's place in its own stream, dense from 0. | |
| **Ordinal** | An event's dense number within a virtual stream, from 0, given after commit by the sequencer. Only these. | never the store provider's `tenant_ordinal`, which is a *partition key* |
| **Partition key** | The value of the partitioning column a store partitioned by tenant gives each tenant, which places its rows in their table partition (Polecat's `tenant_ordinal`). | not a *partition number*, which is SQL Server's slot the value maps to |
| **Locus** | The number a record carries in what it was read from, booked for a later slice (`PENDING.md`). | |

## Streams

| Term | Means | Not |
|---|---|---|
| **`$all`** | The order of positions of one store partition, read whole or for one tenant. | not every event of the system |
| **Virtual stream** | A stream with no events of its own, a predicate over `$all` within one tenant: `$ce-`, `$et-`, `$bc-`, `$streams`, `$category-`, `$de`, `$mde`. | |
| **Category** | A stream name up to its first hyphen after at least one character, otherwise the whole name; never empty. | |
| **Link** | A row of `OrdinalLink`: an event's ordinal in a virtual stream whose ordinals are not columns of the events table. | |
| **Last revision**, **last event** | Where a stream ends: a stream at its last revision, a virtual stream at its last event, or its last ordinal under ordinal numbering. | never a stream's "head" |

## Sequencing

| Term | Means | Not |
|---|---|---|
| **Sequence head** | The position of a store partition's sequence up to which every position is committed or given up; every read and subscription by position stops there. "Head" alone inside a type that already names its sequence. | never a stream's end |
| **High-water mark** | The store providers' word for a sequence head, and the value they keep of it. | |
| **Tail** | What follows a sequence head in a process and wakes whoever waits on it to move. | |
| **Numbered** | How far the sequencer has given ordinals through a sequence. | |
| **Gap** | A position missing below a later one: its transaction is still open, rolled back, or a number the sequence lost. | |
| **Given up** | A gap the tail stops waiting for, so the sequence head moves past it. | |
| **Late commit** | A transaction committing below a position the sequence head or the sequencer already passed; possible only where readers read past an open transaction. | |
| **Transaction horizon** | A journal's point before which the database reports no transaction still open that has written to it: on SQL Server the oldest open transaction, from `DBCC OPENTRAN`; on PostgreSQL the writing sessions in `pg_stat_activity`. Once it passes a marker, nothing more can appear among the positions taken before the marker. | never stored: a question put to the database |
| **Marker** | A moment on a journal's own clock, taken to be judged by its horizon later. | |
| **Horizon checkpoint** | The highest position seen at a look, with a marker taken after it. Every position up to it is final once the horizon has passed the marker and the marker is older than the minimum age. | |
| **Minimum age** | How old a marker must be before its horizon is asked: the moment, on a database where it exists, between a transaction taking a position and the database seeing it write. Zero on SQL Server. | |
| **Settled** | A range of positions final for numbering: a horizon checkpoint over it has been judged final. | |
| **Scrub** | A re-check of a range for events without ordinals: the guardrail, the daily scrub, and the manual one (`PENDING.md`, *The ordinal audit*). | |

## Scope and policy

| Term | Means | Not |
|---|---|---|
| **All tenants** | The scope of a read across every tenant of one store (`TenantScope.AllTenants`). | never across stores |
| **Wildcard** | The `*` value of the tenant header that asks for all tenants. | |
| **Global** | Reserved for two things: a credential not bound to a tenant, and *global numbering* (`Numbering.Global`, `GLOBAL` on the wire), a read numbered by position rather than by ordinal. "Global position" and "global sequence" in older text mean a position and a sequence. | not a synonym for all tenants; a position is global only within its store partition |
| **Tier** | How a feature of a client target is served: the first (appends, reads, subscriptions) is never slowed for parity, the second has a path of its own at best effort, the third is a variance with a client-side workaround. | not "class" |
| **Variance** | A deliberate difference from a client target, recorded in `VARIANCES.md`. | |
