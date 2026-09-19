# Code review

Applies when reviewing a pull request, a diff, or a set of changes — as a
person or as an agent. Language-specific checks follow at the end; this file
is the standard and the format.

## The standard

From Google's engineering practices (CC-BY 3.0):

> Reviewers should favor approving a CL once it is in a state where it
> definitely improves the overall code health of the system being worked on,
> even if the CL isn't perfect.

Consequences:

- **Technical facts and data over opinion.** A style guide or a rules file is
  the authority on style; engineering principles decide design; consistency
  with the existing code decides what is left.
- **Perfect is not the bar; better is.** Ask for what blocks, suggest what
  would improve, and let the rest go.
- **Speed matters.** A review that takes days costs more than the defects it
  finds. Respond within one business day, even if only to say when.

## What to look for, in order

| Look for | Ask |
|---|---|
| **Design** | Does this belong here, shaped like this? Does it fit how the system already works? |
| **Correctness** | Does it do what the description says, including edge cases and failure paths? Would it behave under concurrency, cancellation, partial failure? |
| **Tests** | Do they exist, test behaviour rather than implementation, and would they fail if the code were wrong? |
| **Security and data** | Secrets, PII in logs, injection, trust boundaries, machine-specific paths and personal data ([`../security-reminders.md`](../security-reminders.md)), new dependencies and their licences ([`../dependencies.md`](../dependencies.md)). |
| **Complexity** | Can the next reader understand it? Over-engineering counts. |
| **Naming and comments** | Do names say what things are? Do comments explain *why*, not *what*? |
| **Documentation** | Do docs, `AGENTS.md`, and rules change when behaviour does? |

**Do not review formatting the build already enforces.** StyleCop,
`.editorconfig`, and analyzers run at build time with warnings as errors; a
comment about brace placement or naming style is noise that a green build has
already answered. Review what tools cannot judge.

## How to write a comment

Use [Conventional Comments](https://conventionalcomments.org):

    <label> [(decoration)]: <subject>

    <why, and what would resolve it>

| Label | Use for |
|---|---|
| `issue` | a problem — correctness, security, a broken rule |
| `suggestion` | a concrete improvement |
| `question` | something you need to understand before judging |
| `nitpick` | trivial preference; never blocking |
| `todo` | small, necessary, mechanical |
| `praise` | something done well — say so, specifically |
| `note` | context for the reader, no action |

**Comments are non-blocking by default. `(blocking)` escalates.** An `issue`
that would ship a defect is `(blocking)`; most suggestions are not. Every
blocking comment says what would unblock it.

Always cite the location as `path:line`. Explain the *why*; a bare "change
this" teaches nothing and invites pushback. Prefer offering the fix.

## Verdict

One of: **approve**, **approve with non-blocking comments**, **request
changes** (at least one blocking comment). A verdict without a stated reason
is not a review.

## Broaden the analysis range

A diff shows what changed, not whether the change is right. For every hunk:

- **Read the whole file**, not the hunk — a change is judged in the context it
  lands in.
- **Follow the callers and callees** of anything modified: with Serena
  connected, `find_referencing_symbols`; without, search. A signature change
  is only as safe as its last call site.
- **Read the tests** for the touched code, changed or not — and notice when a
  behaviour changed and no test did.
- **Read the description and the discussion** on a pull request before the
  diff; they say what the author *meant*, which is what the diff is judged
  against.

## Modes

Three ways a review is asked for. The standard and the format above apply to
all of them; what differs is the input and where the output goes.

**Pull request.** Input: title, description, diff, and every existing review
thread — a comment already left is context, not noise. Output, chosen by the
user *before* anything leaves the session: post as a PR review (each finding
as a thread on its `path:line`, a summary body with the verdict), print, or
save a report file. Nothing is posted without showing the user exactly what
will be posted.

**Branch or working tree, no pull request.** Input: the diff against the base
branch (default branch unless told otherwise), plus the broadened context.
Output: print or save. There is nowhere to post.

**Comment triage.** Input: the open review threads on a pull request. For each
thread, produce a verdict — *agree, fix it* / *agree, explain* / *disagree,
push back* / *question, needs the author* — with the proposed reply or change.
Present the whole set to the user as a summary first, then act only on the
threads they confirm: apply fixes (as commits on the branch, per the
pull-request rules), post replies, and resolve threads that are addressed or
answered. A thread is resolved by the person who raised it or with their
consent — resolving to empty the list is not a resolution.

Saved reports go where the user says; suggest `docs/reviews/<ticket>-<date>.md`
— that folder is **git-ignored by default**, so a report is a working note
unless someone force-adds it deliberately — or a scratch location. Never a
path the agent invents into `src/`. The report uses the same format as a
posted review.

The GitHub commands each mode uses are in
[`../source-control/github.md`](../source-control/github.md).

## For an agent

- Read this repository's `AGENTS.md` and the rules under `agentics/rules/` before
  judging — the standard is *this* repository's rules, not general taste.
- Verify, do not assume: run the build and tests when the change is checked
  out; state which claims were checked and which were not.
- Report in the Conventional Comments format with `path:line`, most severe
  first. Zero findings is a valid result; say so plainly.
- Never approve or merge on a person's behalf. Produce the review; the
  verdict is a recommendation.
- If a finding depends on something you could not verify, label it
  `question`, not `issue`.

## Language-specific checks

After the general pass, apply the checklist for the language of the change:

- C# — [`csharp/csharp-code-review.md`](csharp/csharp-code-review.md)
- Markdown and agent-facing docs — [`markdown/markdown-review.md`](markdown/markdown-review.md)
