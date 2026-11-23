<!--
  The .NET parts of a repository's AGENTS.md. agentics/templates/root-AGENTS.md
  is the frame; where it has a line "toolchain: <name>" between four equals
  signs, the part of that name below goes in, for a repository that lists
  dotnet. A part runs from its line to the next part's line.

  Links are relative to the REPOSITORY ROOT, as the frame's are. A part whose
  name ends in -rows is rows of the table it lands in, and nothing else.
-->
<!-- ==== part: intro ==== -->
Toolchain: .NET. Namespace prefix: `{{PREFIX}}`.
<!-- ==== part: layout-standalone-rows ==== -->
| `src/` | production code |
| `test/` | test projects, one per project under test |
| `{{SOLUTION_NAME}}.slnx` | the solution — this repository is a single component |
<!-- ==== part: layout-monorepo ==== -->
A .NET component holds its own `.slnx` — named after its main project — plus
`src/` and `test/`. There is deliberately **no solution at the repository
root**.
<!-- ==== part: layout ==== -->
Build configuration lives **once, at the repository root** — `Directory.Build.props`,
`Directory.Build.targets`, `Directory.Packages.props`, `Tests.props`,
`StyleCop.props`, `stylecop.ruleset`, `stylecop.json`, `.editorconfig`. Every
project inherits it. The targets file stamps every assembly's informational
version with the short commit hash (`1.2.3+abcdef12`, `-dirty` when the tree
is not clean); it never fails the build.

Never add a `Directory.Build.props`, `Directory.Build.targets`, or
`Directory.Packages.props` inside a subfolder — a nested copy silently severs
that subtree from everything above, with a green build and no warning.
[`agentics/rules/layout/dotnet.md`](agentics/rules/layout/dotnet.md), *Config lives at the root only*.
<!-- ==== part: conventions-rows ==== -->
| Projects, assemblies, namespaces | PascalCase, prefixed | `{{PREFIX}}.ProxyGateway` |
| Test projects | `test/<Project>.Tests/` mirrors `src/<Project>/` | `{{PREFIX}}.ProxyGateway.Tests` |
<!-- ==== part: conventions ==== -->
A trailing `.Core` names the assembly, not the namespace — `{{PREFIX}}.Core` has
namespace `{{PREFIX}}` — applied by `Directory.Build.props`; the full rule and
its edge cases are in [`agentics/rules/layout/dotnet.md`](agentics/rules/layout/dotnet.md).
<!-- ==== part: code-intelligence-lsp ==== -->
  **Several solutions:** the C# server holds one — the one listing the most
  projects — and answers for that one only, without saying so. A local
  solution of every project makes it hold them all. Write it before the first
  question of a session and again after adding a project; it is ignored by
  git, never built, and safe to rewrite at any time:

      mkdir -p .lsp && { echo '<Solution>'; git ls-files -co --exclude-standard '*.csproj' | sed 's|.*|  <Project Path="../&" />|'; echo '</Solution>'; } > .lsp/All.slnx

  A type the workspace symbol search does not find is in a project the server
  does not hold; references are then confirmed with Serena or a text search.
<!-- ==== part: code-intelligence ==== -->
Both read the solution's projects — `dotnet restore` first if references look
incomplete.
<!-- ==== part: building ==== -->
### .NET

    dotnet build <solution> --nologo -v:q
    dotnet test  <solution>

**If the build fails only on spacing rules** — `SA1009`, `SA1011`, `SA1111`,
`SA1028`, `SA1518` — do not fix them by hand:

    dotnet format analyzers <solution>

corrects every one at once; rebuild. Run the same command once before a
pull request is opened, and again before its last push if C# changed in
between. It is a repair and a step of the pull request, not of every commit
or every build: the build fails on the same rules, so a commit that builds
is already clean of them, and the pass is slow. It was measured at a minute
on a solution that builds in eight seconds, and `--include` does not make
it cheaper.

**Keep build output out of context.** Build with `--nologo -v:q` and grep for
`error|warning`. Test output is already terse, so grep it for
`total:|failed:|error` and report those lines, never a full log. **Never pass
`--nologo` to `dotnet test`**: the platform's test host rejects it, and not
with a message about the flag — it reports `Zero tests ran` and exit code 5,
which reads as a flake and is not one.

**Warnings are errors.** StyleCop rules set to `Warning` fail the build; rules
at `Info` never surface at build time and appear only in the editor.

A `*.Tests` project inherits the whole test stack from `Tests.props`, so its
`.csproj` holds only `TargetFramework` and a `ProjectReference` —
[`agentics/rules/coding/csharp/csharp-unit-tests-rules.md`](agentics/rules/coding/csharp/csharp-unit-tests-rules.md).
It holds unit tests only. A `*.Tests.Integration` project holds tests that
need a live dependency; a plain `dotnet test` builds it and runs none of it —
pass `-p:RunIntegrationTests=true` to run them.
