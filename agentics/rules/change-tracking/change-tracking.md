# Change tracking

Applies when a piece of work starts, when asked "which ticket is this for",
and when a ticket needs creating or finding. This file is the rules; the
tracker-specific mechanics are in the file the [Trackers](#trackers) section
names. Branches, commits, and change requests are
[`../source-control/source-control.md`](../source-control/source-control.md).

## The rule

Every change is traceable to a ticket, and the ticket's identifier leads — in
the branch name and the change-request title — so history, boards, and
searches line up on one key. A ticket is a sentence of intent, not a design
document: what and why, one line, with context and links in the body.

A ticket is public the moment the repository is. With `publish-safe: true` in
`.agentics.yaml`, title and body refer to other repositories, projects,
customers, systems, and people by role, never by name —
[`../security-reminders.md`](../security-reminders.md), *Names that are not
yours to publish*.

## Settings

`change-tracking.tracker` in the repository's `.agentics.yaml`, asked once
at initialization as a yes or no:

| Value | Meaning |
|---|---|
| `github-issues` | tickets are GitHub Issues; the rule above applies in full |
| `none` | the repository tracks no tickets; branch names and change-request titles carry no identifier, and nothing is created on a tracker |

Nothing else is recorded. A board is a view over the tracker, not a tracker
— see [Boards](#boards).

## Before any work

With `tracker: none` there is nothing to do. Otherwise, if the request names
a ticket, use it; if not, one is needed: in `auto` mode create it and report
the identifier; in `local` and `manual` show the command and wait
(`source-control.mode`; the table is in
[`../source-control/source-control.md`](../source-control/source-control.md),
*For an agent*). A ticket that exists only in the conversation is lost when
the session ends.

Working without a ticket in a repository that tracks them is the user's call,
not the agent's — ask, and if they decline, say so in the change-request
body: `No ticket:` and the reason.

**The one built-in exception is initialization.** The repository, and so its
tracker, does not exist until the first commit is on the default branch; that
commit has no ticket, and the initialization is not opened as one afterwards.
Everything from the second commit on has a ticket.

## Identifier form

The ticket leads: `<ticket>-<kebab-topic>` for the branch,
`#<ticket> <type>(<scope>): <summary>` for the change-request title, and the
tracker's closing keyword with the ticket as the first line of the body. With
`tracker: none` the identifier is simply absent: `<kebab-topic>`,
`<type>(<scope>): <summary>`, no closing line.

The trade-off is deliberate: a leading `#123` is not what strict Conventional
Commits parsers expect. This toolkit does not run one; if a repository adopts
`commitlint` or similar, move the ticket to the end
(`feat(billing): retry failed charges once (#123)`) and say so in `AGENTS.md`.

## Boards

A board is a view over tickets with a card per ticket; the ticket is the
record, and the host keeps the two in step on its own (a board that adds new
tickets and moves closed ones is a one-time setting on the board, not the
agent's work). The agent therefore never touches a board unless asked; then
it checks the credential's scope first — board access is never assumed the
way tracker access is — works the request out on demand, and treats it as a
host action under the mode like any other. A card created on a board without
a ticket behind it is a note, not a ticket — it has no identifier — and
becomes one through the tracker's own convert step before any branch is
named.

## Trackers

| Tracker | File |
|---|---|
| GitHub Issues | [`github.md`](github.md) |

Only GitHub today. For Jira and similar, the rules transfer with the
identifier form changed (`PROJ-123-billing-retry`, `PROJ-123 feat(...)`) and
the closing keyword replaced by the tracker's own link syntax; add a sibling
file here and a row above rather than generalising this one.
