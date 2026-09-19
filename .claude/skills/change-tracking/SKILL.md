---
name: change-tracking
description: Create or find the ticket a piece of work belongs to, per this repository's change-tracking rules. GitHub Issues only, for now.
when_to_use: When asked which ticket a change is for, or to create or find a ticket or issue. Branches, commits, and pull requests are /source-control. A Projects board is out of scope - on an explicit request, check the token's scope first and work it out on demand, per the tracker file.
argument-hint: new "<what and why>" | find <#n | words>
---

Change tracking per **this repository's rules**. GitHub Issues is the only
supported tracker today; for anything else, say so and stop.

## Find the rules

First `.agentics.yaml` at the repository root: `change-tracking.tracker`
is `github-issues` or `none` - with `none` there is nothing to create and
this skill says so and stops; `source-control.mode` says whether creating a
ticket asks first. Then read the rule in the first location that exists,
then the tracker file it names. Never work from memory.

| Rule | Initialized repository | dc-agentics toolkit |
|---|---|---|
| change tracking - the ticket rule, the setting, the initialization exception, boards; names the tracker file | `agentics/rules/change-tracking/change-tracking.md` | `repo/payload/agentics/rules/change-tracking/change-tracking.md` |

## Actions from `$ARGUMENTS`

| Argument | Do | Where the procedure is |
|---|---|---|
| `new "<what and why>"` | create the ticket and report its number | change tracking, *Before any work*; tracker file, *Tickets* |
| `find <#n \| words>` | show the ticket, or the tickets matching the words | tracker file, *Tickets* |

## Always

A ticket is a sentence of intent, not a design document. Creating one is a
host action: it follows `source-control.mode` in the source-control rules,
*For an agent*. Nothing is invented that the user did not give - not the
ticket's intent, not which ticket the work belongs to.
