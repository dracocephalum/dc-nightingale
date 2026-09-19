---
name: source-control
description: Start work on a ticket with a correctly named, issue-linked branch; commit; open a pull request; or apply the repository's merge settings - all per this repository's source-control and change-tracking rules. GitHub only, for now.
when_to_use: When asked to start work on a ticket or issue, create or name a branch, commit changes, open or update a pull request, publish a repository, or set up a repository's merge settings.
argument-hint: publish | start <#issue | "what to do"> | commit | pr | setup
---

Source control per **this repository's rules**. GitHub is the only supported
host today; for anything else, say so and stop.

## Find the rules

First `.agentics.yaml` at the repository root: its `source-control.mode`
(`auto` | `local` | `manual`; absent = `manual`) decides which of the steps
below run without asking and which stop for a yes - the table is in the
source-control rules, *For an agent*. Then read each rule in the first
location that exists, then the host file the source-control rules name. Never
work from memory.

| Rule | Initialized repository | dc-agentics toolkit |
|---|---|---|
| source control - branches, commits, size, opening, review, merge, agent guardrails; names the host file | `agentics/rules/source-control/source-control.md` | `repo/payload/agentics/rules/source-control/source-control.md` |
| change tracking - the ticket that must exist first; names the tracker file | `agentics/rules/change-tracking/change-tracking.md` | `repo/payload/agentics/rules/change-tracking/change-tracking.md` |

## Actions from `$ARGUMENTS`

| Argument | Do | Where the procedure is |
|---|---|---|
| `publish` | put a local repository that has no remote on the host | host file, *Publishing a repository* |
| `start <#issue \| "description">` | ensure a ticket exists (`/change-tracking new` when it does not), create the linked branch, switch to it | change tracking, *Before any work*; tracker file, *Tickets* |
| `commit` | stage what the user indicates and commit | source control, *Commits* |
| `pr` | open a draft pull request from the template | source control, *Opening a change request*; host file, *Pull requests* |
| `setup` | apply the merge settings and the default-branch ruleset once | host file, *Repository settings* |

## Always

The *Guardrails an agent must not skip* and *For an agent* sections of the
source-control rules apply to every mode - branch confirmed before every
commit, nothing side-effecting run to diagnose, the command shown before
anything is created on the host, nothing invented that the user did not give.
