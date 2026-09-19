---
name: review
description: Review a pull request, a branch, or a set of files against this repository's rules, or triage the review comments on a pull request - producing Conventional Comments with a verdict, posted, printed, or saved as a report.
when_to_use: When asked to review, check, critique, or look over a pull request, a diff, a branch, or specific files; or to go through, answer, address, or resolve review comments on a pull request.
argument-hint: "[PR#] [branch] [paths...] [comments PR#] [--report <file>]"
---

Review against **this repository's rules**, not general taste. The built-in
`/code-review` hunts bugs with its own machinery; this skill judges compliance
with the rules and reports in a fixed format. They complement each other and
are not chained - run the built-in separately if a bug pass is wanted.

## Find the standard

1. `agentics/rules/coding/code-review.md` - an initialized repository
2. `repo/payload/agentics/rules/coding/code-review.md` - the dc-agentics toolkit

Read the first that exists, then the language checklists and the rules
documents it names, then the repository's `AGENTS.md`. Never review from
memory.

## Choose the mode from `$ARGUMENTS`

| Arguments | Mode in the standard |
|---|---|
| a number, e.g. `42` | *Pull request* |
| `comments 42` | *Comment triage* - the open review threads on PR 42 |
| a branch name | *Branch* - its diff against the base branch |
| one or more paths | *Files* - those files in full (also the mode when there is no git history, as in the toolkit itself) |
| nothing | *Working tree* - uncommitted plus unpushed changes |

`--report <file>` saves the review there instead of printing.

## Then

Follow the standard's *Broaden the analysis range*, *Modes*, and *For an
agent* sections as written: what leaves the session is shown in full and
confirmed first, the verdict is a recommendation, and in comment triage the
summary of verdicts is the confirmation step. The GitHub commands are in
`agentics/rules/source-control/github.md` - in the toolkit,
`repo/payload/agentics/rules/source-control/github.md`.
