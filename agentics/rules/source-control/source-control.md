# Source control

Applies when publishing a repository for the first time, creating a branch,
writing commits, or opening, updating, or merging a change request — a pull
request, a merge request, whatever the host calls it. This file is the rules;
the host-specific mechanics are in the file the [Hosts](#hosts) section names.

The standard, borrowed from Google's engineering practices: a change should be
**small, described so a stranger understands what and why, and verified before
anyone else spends time on it.**

## A ticket first

Every change is traceable to a ticket, and the ticket number leads in the
branch name and the change-request title — the rule and its reasoning are
[`../change-tracking/change-tracking.md`](../change-tracking/change-tracking.md)'s.
How a ticket is created and linked, and the form the
names take when a repository tracks no tickets at all, is the tracker's
business: [`../change-tracking/change-tracking.md`](../change-tracking/change-tracking.md).

## Branches

- Name: `<ticket>-<kebab-topic>`, e.g. `123-billing-retry`. The change type
  lives in the commits, not the branch name.
- Branch from the default branch. Short-lived: a branch older than a few days
  is a sign the change is too large — split it.
- Never commit directly to the default branch.

## Commits

Conventional Commits, so history is scannable and release notes can be
generated:

    <type>(<scope>): <imperative summary, ≤ 72 chars>

    <why, not what - the diff shows what>

Types: `feat`, `fix`, `refactor`, `test`, `docs`, `build`, `ci`, `chore`,
`perf`. `!` after the type marks a breaking change. The first line is a
sentence a future developer reads in `git log --oneline` — "Fix bug" and "WIP"
are not summaries.

**Wrap the message body at ~72 columns.** Git stores it verbatim and never
reflows it, so `git log` in a narrow terminal shows exactly the lines that were
written. This is the opposite of a pull-request body — see below — and the two
are confused often enough to be worth stating separately.

Commits are signed (the machine setup enforces this) and contain no secrets
(the pre-commit hook enforces this). A commit that fails either is not fixed
by `--no-verify`.

With `publish-safe: true` in `.agentics.yaml`, the message names no other
repository, project, customer, system, or person that is not already in the
tracked files — by role instead. Nothing enforces it; a message is published
with the push, and history is not edited afterwards. The rule and its one test
are in [`../security-reminders.md`](../security-reminders.md), *Names that are
not yours to publish*, and the same holds for everything below that reaches
the host.

## Size

One concern per change request. As a guide, **under ~400 changed lines** of
non-generated code; a reviewer's attention drops sharply past that and defects
get through. If a change needs more, split it into a sequence — refactor
first, behaviour second — and open them as separate, dependent requests.

## Opening a change request

Before opening:

1. `dotnet build` and `dotnet test` green — the reviewer should never be the
   one to discover a red build.
2. The licence check passes (`agentics/rules/dependencies.md`) if any package
   changed.
3. Read your own diff top to bottom as a reviewer would. Remove debug code,
   stray files, unrelated formatting.
4. The description still matches the code — descriptions written at the
   start of a change drift by the end.
5. With `publish-safe: true`, the title and body name nothing that is not
   already in the tracked files — *Commits* above; the body is where the
   context an agent worked in leaks most easily.

Then:

- **Title**: `#<ticket> <type>(<scope>): <summary>` — ticket first, then the
  conventional summary, because squash-merge makes it the commit message.
- **Body** follows the repository's change-request template. **Bullet
  points, not paragraphs**: one bullet per change in behaviour or structure
  under *What*; a **checklist** wherever the section is a list of actions —
  what was verified, what a person must do at merge or deploy time. The first
  line of the body is the tracker's closing keyword and the ticket.
- **A non-trivial change gets a *How*.** The approach and the reasoning
  behind it, in the form that makes them easiest to grasp later — a list of
  steps, a table of the options considered, a mermaid diagram of the flow.
  It is written for the reader who opens this request in a year, person or
  agent, and asks why it was done this way; the diff will not tell them.
  Trivial changes skip it.
- **The description scales with the diff.** Two sentences describe a
  two-file change; they do not describe a large one. When a request is
  legitimately large — a generated migration, a new component, a sequence
  that could not be split — the description is what makes it reviewable:
  bullets grouped by area, a reading order under *Notes for the reviewer*,
  every manual step under *Actions*. A reviewer should be able to navigate
  the diff from the description alone.
- **Never hard-wrap the body.** A description is typed into a web form, not
  stored as a file: the host renders every newline as a line break, so prose
  wrapped at 80 columns stays at 80 columns for every reader and wraps a second
  time, raggedly, on a narrow window. **One paragraph per line**, blank lines
  between paragraphs. Lists, tables and code fences are unaffected. This is the
  reverse of the rule for `.md` files in the repository, which are wrapped
  because their renderer joins the lines of a paragraph — the question is
  always whether the audience is a renderer or a diff.
- **Draft** until it is ready for review. A draft says "look if you like, do
  not spend review effort yet."
- Request reviewers explicitly; code owners are the default choice.

## During review

- Respond to every comment — a fix, a question, or a reasoned disagreement.
  Silently pushing a change without answering leaves the reviewer guessing.
- Push follow-up commits; do not rewrite history under a reviewer. Rebase
  only to resolve conflicts, with `--force-with-lease`, and say so in a
  comment.
- Resolve a conversation only when its point is addressed, not to clear the
  list.

## Merging

- **Squash, always**: one conventional commit per change request on the
  default branch, title as the message. Merge commits and rebase-merge are
  disabled at the repository level, not left to habit.
- **The branch is deleted on merge** — also a repository setting.
- The merge is the author's, after approval and green checks — not the
  reviewer's.

## Guardrails an agent must not skip

State can be lost between steps — a long session, a compaction, a model
switch. These checks cost nothing and catch the failure that follows:

- **Confirm the branch before every commit.** `git branch --show-current`
  must equal the branch you intend. A commit on the wrong branch is the most
  common way a change ends up orphaned; verify, do not assume.
- **Never run a side-effecting command to "diagnose".** Branch creation,
  `git commit`, `git push`, and any host command that creates something all
  change state. To inspect, use read-only commands (`git status`,
  `git branch -vv`, `git log`, the host's `view` commands). A command run to
  investigate a failure must not be able to cause a new one.
- **After any branch switch, verify what is under you** before working:
  `git log --oneline -3` should show the commits you expect. A branch that
  unexpectedly sits at the base commit means the switch did not carry your
  work.
- **When recovering, inspect before you act.** `git branch -vv` and
  `git log --graph --all` first; understand the divergence, then fix it. Bring
  files across with `git checkout <branch> -- <path>` only after confirming
  what that overwrites — it silently replaces the whole file.

## For an agent

What the agent may do without asking is `source-control.mode` in the
repository's `.agentics.yaml`, read before the first action of any
source-control task. Absent, or a value not listed, means `manual`. The user
can override it in conversation ("auto for this one"); that holds for the
session and is never written back.

| Step | `auto` | `local` | `manual` |
|---|---|---|---|
| Edit the working tree | agent | agent | agent |
| Create the ticket | agent, reports | ask | ask |
| Create the branch | agent | agent, once the ticket exists | ask |
| Commit | agent | agent, locally | ask |
| Push | agent | ask | ask |
| Open the change request | agent, as a draft | ask | ask |
| Merge | never | never | never |

*Ask* means show the exact command and wait for a yes; *agent* means run it
and say what was done. The linked branch is created on the host, and the yes
to the ticket covers it: the branch is the ticket's, so `local` does not ask
twice. In `local`, the first commit still needs the ticket:
the branch name carries its number and nothing is committed on the default
branch, so the work stays uncommitted in the tree until the user says yes to
the ticket. "Not yet" leaves it uncommitted — never commit on the default
branch or on an unnamed branch to get around that.

In every mode:

- Open change requests as **drafts** unless told otherwise, and never merge.
- Write multi-line bodies to a file first — the host file says why a
  command-line body is not safe.
- Do not force-push, rebase, or close a request that has review activity
  without being asked.
- The commit and push go through the signing and secret-scan setup as
  configured; if either fails, report it — do not bypass.
- Ask for what is missing — the ticket, the scope, the summary — rather than
  inventing it.
- Anything left unfinished, blocked, or undecided goes into the repository's
  `TODO.md`, not only into the conversation.

## Hosts

The mechanics — publishing, the exact commands, repository settings — depend
on where the repository lives:

| Host | File |
|---|---|
| GitHub | [`github.md`](github.md) |

Only GitHub today. For another host, add a sibling file here with the same
sections, and a row above; do not generalise this file around it.
