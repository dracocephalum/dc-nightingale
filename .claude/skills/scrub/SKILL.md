---
name: scrub
description: Sweep this repository for inconsistency and drift - markdown lint, unresolved links, unfilled placeholders, mixed line endings, privacy, settings that no longer match reality, an incomplete AGENTS.md index, stale TODO entries, and drift in the files forked from dotnet new templates. Reports; does not fix.
when_to_use: When asked to scrub, sweep, audit, or health-check a repository; to look for drift, staleness, or inconsistency; or after a move, rename, or restructure.
argument-hint: "[paths...]"
---

A consistency and drift pass. **Not a bug hunt** - that is the built-in
`/code-review`. **Not a rules review** - that is `/review`. Building, testing
and dependency licences are deliberately outside it, so a scrub stays cheap
enough to run without deciding to.

## Find the standard

1. `agentics/rules/scrub.md` - an initialized repository
2. `repo/scrub.md` - the dc-agentics toolkit, which adds its own checks on top
   of `repo/payload/agentics/rules/scrub.md`

Read the first that exists and follow it as written. Never scrub from memory:
the checks exist because each one caught something that review did not.

## Scope

The whole repository, always. Most checks are repository-wide by nature - the
`AGENTS.md` index, `.agentics.yaml` truthfulness, `TODO.md`, baseline drift,
duplication - and a narrowed pass reported as clean is worse than no pass. If
`$ARGUMENTS` names paths, run everything and lead the report with those.

## Report

A findings table - number, check, `pass` / `fail` / `skipped`, and detail - with
what would close each failure. **Change nothing.** Fixes happen only when asked,
as a separate reviewable change, and a baseline drift is a decision for the user
rather than a file to rewrite.
