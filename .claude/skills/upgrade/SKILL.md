---
name: upgrade
description: Four operations under one verb - bring the dc-agentics toolkit's own pinned versions current; sync a repository it initialized to a newer toolkit commit, replacing the toolkit-owned folders whole with the script in repo/sync.sh; convert a standalone repository to a monorepo; or adopt a toolchain the repository does not list yet. Plans first; applies second.
when_to_use: When asked to upgrade, update, refresh, or bring current a repository initialized with dc-agentics or the toolkit's own SDK baselines and package versions; to convert a repository from standalone to monorepo, move it under categories, or restructure its layout; or to add a toolchain to a repository, such as Go beside .NET.
argument-hint: "[toolkit | target <path> | monorepo | toolchain <name>]"
---

`agentics/` and the toolkit's shims in a target are the toolkit's: a sync
replaces them whole, by script, and the repository's own rules live outside
them. The recorded commit says what changed, not what to merge.

## Find the standard

`repo/upgrade.md` in a dc-agentics checkout. Operation 2 needs **both**
repositories present, because its script runs from the toolkit checkout; ask
for the checkout path rather than guessing it. Read the file and follow it -
what the script does, what it reports for you to apply by intent, and the
one-time adoption of the guidelines block are all there.

## Choose the operation from `$ARGUMENTS`

| Arguments | Operation |
|---|---|
| `toolkit` | 1 - bring the toolkit's own pins current |
| `target <path>`, or a path | 2 - sync that repository to the current toolkit: `sh repo/sync.sh --plan <path>`, read, then `--apply` |
| `monorepo` | 3 - convert a standalone repository to a monorepo |
| `toolchain <name>` | 4 - adopt a toolchain: `agentics/rules/layout.md`, *Adopting a toolchain*, in the repository itself |
| nothing | ask which; they touch different repositories |

Operations 3 and 4 need **no toolkit checkout**; run them from inside the
repository. The document for 4 is the repository's own.

For operation 3: Run from a toolkit checkout instead, it first checks that the
two agree, and declines rather than mixing a payload delta into a layout change.

## Then

The tree being changed must be clean - `git status --porcelain` empty,
untracked included - or decline and say so; the document says why, and what
the one-command undo is that a clean start makes possible.

Produce the plan first: what changed between the two commits, what it would do,
and what is left for you to apply by intent. Apply as a second step, and let
`source-control.mode` in the target's `.agentics.yaml` decide what may be
committed without asking; the script itself never commits.

Operations 2, 3 and 4 finish with the build and the tests, by the commands
`AGENTS.md` gives, and `/scrub` - the scrub checks are the post-upgrade checks. Operation 1 has nothing to build
in the toolkit; the document says how a bump is proven.
