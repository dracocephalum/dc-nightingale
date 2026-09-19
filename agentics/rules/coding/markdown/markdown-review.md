# Markdown review checklist

Applies after the general pass in [`../code-review.md`](../code-review.md),
to documentation
and to agent-facing files — `AGENTS.md`, anything under `agentics/rules/`,
skill shims. Two layers: mechanics, then content.

## Mechanics — run the linter, do not eyeball

    npx --yes markdownlint-cli2@0.23.2 "**/*.md" "#node_modules"

`--yes` because it is rarely on `PATH` and npx otherwise stops to ask, which
reads as a hang. The version is pinned so a new release cannot change the
verdict mid-review — bump it deliberately. `"#node_modules"` keeps vendored
markdown out. Read the `Summary:` line: the exit code says pass or fail and
nothing about how much.

**This checklist is about files.** Issue bodies, change-request descriptions
and review comments are not files: the host renders every newline in them as a
line break, so they are written unwrapped, one paragraph per line. See
[`../../source-control/source-control.md`](../../source-control/source-control.md),
*Opening a change request*.

The repository ships `.markdownlint.yaml`; it relaxes line length for prose
and disables table-style rules that add nothing. What remains is real:
broken tables (`MD056`), lists glued to paragraphs (`MD032`), spaces inside
code spans (`MD038`), a line that accidentally starts with `-` and becomes a
bullet. Fix these; do not argue with them.

## Content — what a linter cannot judge

For any document an agent will act on:

| Check | Why |
|---|---|
| **The trigger is stated** — "when you are X" / "applies when Y" | a rule with no trigger is applied everywhere or nowhere |
| **Decision first, rationale after** | a truncated read still gets the rule |
| **No duplication** of a rule stated elsewhere | two copies drift; link instead |
| **Commands were run**, not typed from memory | a snippet that has never executed is a liability |
| **Links resolve** from the file's own directory | a dead link is a rule nobody reaches |
| **No placeholders left** — `{{…}}`, `TBD`, `TODO`, `(path)` | a template that shipped unfilled |
| **No local paths, personal data, or secrets** | [`security-reminders.md`](../../security-reminders.md) — it will be pushed |
| **Portable shell** — POSIX `sh`, no GNU-only flags, no backslash paths | the reader may not be on Windows |
| **Length is earned** | every always-loaded line costs every session; on-demand files may be longer |

## Structure — is it shaped for an agent?

An agent reads a document to decide *whether it applies* and *what to do*,
usually under a token budget and often only partially. Judge the shape, not
just the words:

| Check | Pass looks like |
|---|---|
| **Headings name tasks, not topics** | "Adding a project", "Verify" — not "Projects", "Misc" |
| **The first screen answers "does this apply to me?"** | trigger sentence, then scope, before anything else |
| **Lookups are tables, rules are bullets, rationale is prose** | a smell→rule table beats three paragraphs; a rule is one bullet, decision first |
| **Runnable things are code blocks that run as written** | no `$` prompts, no prose inside a command; placeholders in angle brackets are called out |
| **References resolve mechanically** | relative links from the file's own directory, or repository-root-relative paths in text; never "see above", "as discussed", "the earlier section" |
| **Every procedure ends in a verification** | a command and what its output must contain |
| **Size matches load frequency** | always-loaded files (`AGENTS.md`) stay under ~150 lines; on-demand files may run longer but split at ~200 |
| **Format is the cheapest that carries the meaning** | terse markdown; JSON/YAML only for genuinely tabular data (measured: JSON costs ~1.6× the tokens of the same rules as bullets, base64 ~3×) |
| **Nothing is stated twice across files** | one home per rule; other files link to it |

Findings here are `suggestion` unless the document cannot be acted on
mechanically — an unresolvable reference or a command that does not run as
written is an `issue`.

Link check: the script is check 2 in [`../../scrub.md`](../../scrub.md), and
lives there only. It filters code blocks and inline code first, because a
document that describes a link check contains link-shaped text that is not a
link, and it names the one legitimate exception — `agentics/templates/`, whose
links are relative to the repository root where their output lands.

## Labels

Mechanics findings are `todo` (fix, no discussion). A missing trigger, a
duplicated rule, or an unverified command is an `issue`. Wording preferences
are `nitpick` and never block.
