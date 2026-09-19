---
name: upgrade
description: Three operations under one verb - bring the dc-agentics toolkit's own pinned versions current; upgrade a repository it initialized to a newer toolkit commit, deciding each file against the target's own copy; or convert a standalone repository to a monorepo. Plans first; applies second.
when_to_use: When asked to upgrade, update, refresh, or bring current a repository initialized with dc-agentics or the toolkit's own SDK baselines and package versions; or to convert a repository from standalone to monorepo, move it under categories, or restructure its layout.
argument-hint: "[toolkit | target <path> | monorepo]"
---

Upgrading is diff-and-decide, not copy-over. The toolkit records what a
repository was initialized from, and that recorded commit is the merge base
that tells a customized file from an untouched one.

## Find the standard

`repo/upgrade.md` in a dc-agentics checkout. Operation 2 needs **both**
repositories present, because it diffs two commits of the toolkit; ask for the
checkout path rather than guessing it. Read the file and follow it - the
per-path merge policy and the diff mechanics are both there, and neither is
guessable.

## Choose the operation from `$ARGUMENTS`

| Arguments | Operation |
|---|---|
| `toolkit` | 1 - bring the toolkit's own pins current |
| `target <path>`, or a path | 2 - upgrade that repository to the current toolkit |
| `monorepo` | 3 - convert a standalone repository to a monorepo |
| nothing | ask which; they touch different repositories |

Operation 3 needs **no toolkit checkout**; run it from inside the repository
being converted. Run from a toolkit checkout instead, it first checks that the
two agree, and declines rather than mixing a payload delta into a layout change.

## Then

The tree being changed must be clean - `git status --porcelain` empty,
untracked included - or decline and say so; the document says why, and what
the one-command undo is that a clean start makes possible.

Produce the plan first: what changed between the two commits, what it would do
to each file, and what needs a decision. Apply as a second step, and let
`source-control.mode` in the target's `.agentics.yaml` decide what may be
committed without asking.

Operations 2 and 3 finish with `dotnet build`, `dotnet test`, and `/scrub` -
the scrub checks are the post-upgrade checks. Operation 1 has nothing to build
in the toolkit; the document says how a bump is proven.
