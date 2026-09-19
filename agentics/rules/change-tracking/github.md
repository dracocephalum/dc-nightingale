# Change tracking on GitHub

The mechanics of [`change-tracking.md`](change-tracking.md) on GitHub: the
ticket is a GitHub Issue and its number is the identifier. Read the rules
first. How to reach GitHub — an MCP server or `gh` — and the pull-request and
review commands are in
[`../source-control/github.md`](../source-control/github.md).

## Permissions

The default `gh auth login` (`repo`) covers everything here. Nothing about a
Projects board is set up in advance: a board request is handled on demand,
and the first step is `gh auth status` — the board scopes (`read:project` to
look, `project` to change) are never assumed to be on the token the way
`repo` is. When they are missing, hand the user
`gh auth refresh -h github.com -s read:project,project` (it opens a browser)
and stop until it has run.

## Tickets

| Need | Command |
|---|---|
| create | `gh issue create --title "<what and why, one line>" --body "<context, links>"` — take the number it returns |
| find | `gh issue list --search "<words>"`; `gh issue view <n>` |
| the branch linked to it | `gh issue develop <n> --name <n>-<kebab-topic> --base main`, then `git fetch origin <branch>` and `git switch <branch>` |
| what is linked | `gh issue develop --list <n>` |

Create the branch and check it out in two steps rather than with
`--checkout`, which can fail on its own internal `git` call after the branch
and the link already exist on GitHub — it did on the first run through this
flow, leaving the work on `main`. The two-step form uses the same git
configuration as every other command and is verifiable at each step. The
user may override the naming (an existing convention, a hotfix with no
ticket); record the reason in the pull-request body.

A ticket body is not wrapped, for the same reason a pull-request body is not:
GitHub renders it with hard line breaks on, so wrapped prose stays wrapped for
every reader. One paragraph per line. See
[`../source-control/github.md`](../source-control/github.md).

## Boards

Nothing routine. A Projects board keeps itself in step with issues once its
own workflows are on (*Auto-add to project* with the filter `is:issue`,
*Item closed → Done* — web UI, once). When the user asks for something on a
board, work it out from `gh project --help` and the GraphQL API after the
scope check above, and show the command first as for any host action. One
fact worth knowing in advance: a card created on the board is a draft with
no issue number, no `Closes`, and no linked branch — it becomes a ticket
through the board's *Convert to issue*, and work starts from the issue that
produces.
