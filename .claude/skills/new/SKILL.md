---
name: new
description: Add something to this repository - a C# project (library, service, job, tool, or a test project), or a whole new component with its own solution under a category. Asks for the choices; applies the repository's mechanics without asking, because the build enforces those anyway.
when_to_use: When asked to create, add, scaffold, or set up a new project, component, service, library, console app, worker, or test project in this repository.
argument-hint: "[project|component] [kind] [name]   e.g. project classlib Billing, component billing"
---

Currently supports **C# only** - for any other language or framework, say so
and stop.

## Find the procedure

1. `agentics/rules/coding/csharp/csharp-new-project.md` - an initialized repository
2. `repo/payload/agentics/rules/coding/csharp/csharp-new-project.md` - the toolkit

Read the first that exists and follow it exactly. Both modes, what is asked and
what is not, where things go, and what "done" means are all there; do not work
from memory. If neither exists, this repository was not initialized from the
toolkit - say so and stop.

## Where it is being run matters

| Situation | Do |
|---|---|
| inside an initialized repository | proceed; it carries everything needed |
| from a toolkit checkout, against a target path | confirm the two agree first - the toolkit's `repo/version-check.md` |
| scaffolding **into** the dc-agentics toolkit itself | refuse; there are no C# components here to add to |

When that check declines, the way out is always the same: run it from inside
the target.

## Modes, from `$ARGUMENTS`

| Argument | Mode |
|---|---|
| `project` | one project in an existing component; in a monorepo, ask which |
| `component` | a new solution under a category, which is asked for |
| nothing | ask which; adding a second component to a standalone repository means converting the layout first, via `/upgrade` |
