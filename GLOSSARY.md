# Glossary

The words this repository uses for its own ideas, and the ones it keeps apart.
A term here means the same in code, comments and documents; a name that would
blur two of them is the wrong name. `DESIGN.md` says why each thing is shaped
the way it is.

## Roles

| Term | Means | Not |
|---|---|---|
| **Store provider** | An event-sourcing library the gateway runs on and leaves the heavy lifting to, the writes above all: Polecat on SQL Server, Marten on PostgreSQL (planned). It provides the event storage, its sessions and schema, and the high-water agent. | not "the store" alone, which is the database it manages |
| **Client target** (protocol target) | A product whose clients the gateway means to serve through a protocol layer compatible with its own: KurrentDB. The older documents call it *the reference event store*, from when it was the only one. | not a store provider; the gateway never runs on it |
| **Gateway** | This server: the protocol, the subscriptions, and what it adds to a store provider. | |
| **Backend** | The gateway's implementation of its ports over one store provider, `Dracocephalum.Nightingale.Server.Polecat`. | |

## Numbers

| Term | Means | Not |
|---|---|---|
| **Position** | An event's place in the store's sequence, `seq_id`: global, rising, sparse. | not an offset in a log, as a client target's |
| **Revision** | An event's place in its own stream, dense from 0. | |
| **Ordinal** | An event's dense number within a virtual stream, from 0, given after commit by the sequencer. Only these. | never the store provider's `tenant_ordinal`, which is a *partition key* |
| **Partition key** | The value of the partitioning column a store partitioned by tenant gives each tenant (Polecat's `tenant_ordinal`). | not a *partition number*, which is SQL Server's slot the value maps to |
| **Locus** | The number a record carries in what it was read from, booked for a later slice (`PENDING.md`). | |

## Streams

| Term | Means |
|---|---|
| **Virtual stream** | A stream with no events of its own, a predicate over `$all`: `$ce-`, `$et-`, `$bc-`, `$streams`, `$category-`, `$de`, `$mde`. |
| **Category** | A stream name up to its first hyphen after at least one character, otherwise the whole name; never empty. |
| **Link** | A row of `OrdinalLink`: an event's ordinal in a virtual stream whose ordinals are not columns of the events table. |

## Sequencing

| Term | Means |
|---|---|
| **High-water mark** | The position up to which every position is either committed or given up; every read and subscription by position stops there. |
| **Gap** | A position missing below a later one: its transaction is still open, rolled back, or a number the sequence lost. |
| **Given up** | A gap the tail stops waiting for, so the mark moves past it. |
| **Late commit** | A transaction committing below a position the mark or the sequencer already passed; possible only where readers read past an open transaction. |
| **Transaction horizon** | The point before which the database reports no transaction still open that could commit into the events table: from `sys.dm_tran_database_transactions` on SQL Server, from the writing sessions in `pg_stat_activity` on PostgreSQL. Once it passes the moment a range of positions was passed, nothing more can appear in that range. |
| **Settled** | A range of positions final for numbering: the transaction horizon has passed it and it is older than the minimum age. |
| **Scrub** | A re-check of a range for events without ordinals: the guardrail, the daily scrub, and the manual one (`PENDING.md`, *The ordinal audit*). |

## Scope and policy

| Term | Means | Not |
|---|---|---|
| **All tenants** | The scope of a read across every tenant (`TenantScope.AllTenants`). | |
| **Wildcard** | The `*` value of the tenant header that asks for all tenants. | |
| **Global** | Reserved for credentials not bound to a tenant, and for positions. | not a synonym for all tenants |
| **Tier** | How a feature of a client target is served: the first (appends, reads, subscriptions) is never slowed for parity, the second has a path of its own at best effort, the third is a variance with a client-side workaround. | not "class" |
| **Variance** | A deliberate difference from a client target, recorded in `VARIANCES.md`. | |
