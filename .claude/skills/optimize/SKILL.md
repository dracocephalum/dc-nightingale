---
name: optimize
description: Measure what this repository's documents cost an agent to read and, only on request, reduce it - compaction with no rule lost, or removal of what a named model does unprompted, logged so it can be restored. Measures by default; changes nothing unless asked.
when_to_use: When asked to optimize, compact, slim, shrink or trim documentation or rules; to measure context or token cost of the documents; or to remove instructions a model follows by default. Not for consistency or drift - that is /scrub.
argument-hint: "[measure | compact [paths...] | defaults <model> [paths...]]"
---

A cost pass over the documents agents read. **Not a consistency check** -
that is `/scrub`, which asks whether the documents are true; this asks what
they cost. The two never run as one.

## Find the standard

1. `agentics/rules/optimize.md` - an initialized repository
2. `repo/optimize.md` - the dc-agentics toolkit, which adds its own
   adjustments on top of `repo/payload/agentics/rules/optimize.md`

Read the first that exists and follow it as written.

## Operation

`$ARGUMENTS` names it. With none, or with anything that does not name one
exactly, the operation is **measure**.

- `measure` - report weights and when each is paid, then say which of the
  other two the numbers justify. Changes nothing.
- `compact` - remove verifiable redundancy only, with the before-and-after
  rule lists the document requires. One reviewable change.
- `defaults <model>` - only with the model named, and only under every guard
  in the document. "Optimize" or "compact" never means this.

## Report

Measure: the table by load group, with totals and the arithmetic of what a
pass would cost and save. Compact and defaults: what was removed, where each
rule now lives, and for defaults that the removals are asserted, not verified.
