# C# code review checklist

Applies after the general pass in [`../code-review.md`](../code-review.md).
Every item here is something the build **cannot** catch — StyleCop, analyzers
and warnings-as-errors already reject formatting, naming style, and the
banned-API list. The authority for each item is
[`csharp-coding-rules.md`](csharp-coding-rules.md),
[`csharp-ef-core-rules.md`](csharp-ef-core-rules.md), and
[`csharp-unit-tests-rules.md`](csharp-unit-tests-rules.md); cite the rule.

| Smell in the diff | Rule | Label |
|---|---|---|
| PII, token, or payload in a log, metric, or span | Observability | `issue (blocking)` |
| `LogInformation` on a hot path, or as the default level | Observability | `issue` |
| `userId`, `sessionId`, request id as a metric tag | Observability | `issue` |
| New `DateTime` property, parameter, or return | Time | `issue` |
| `new DateTimeOffset(dateTime)` on a value of unknown `Kind` | Time | `issue (blocking)` — silently assumes local |
| `DateTime.Now` / `UtcNow` in production code — the build should have caught it; if it did not, `BannedSymbols.txt` is not attached | Time | `issue` |
| `async` method without a `CancellationToken`, or one that drops it | Types and APIs | `issue` |
| `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` | Types and APIs | `issue (blocking)` |
| Public API returning `List<T>` or `T[]` | Types and APIs | `suggestion` |
| Small in-memory list wrapped in `IAsyncEnumerable<T>` | Types and APIs | `suggestion` |
| Implicit conversion operator on an id or value object | Types and APIs | `issue` |
| AutoMapper / reflection mapper introduced | Types and APIs | `issue (blocking)` |
| `!` to silence a nullable warning | Types and APIs | `issue` |
| `throw new Exception(...)` or `catch (Exception)` swallowing | Design | `issue` |
| Validation or not-found expressed as a throw where the component uses result records | Design | `suggestion` |
| Entity or `DbSet` type crossing the API boundary | Design | `issue` |
| Enum property without `.HasConversion<string>().HasMaxLength(…)` | EF Core, Model | `issue` |
| New `DbContext` without a `ModelConventions.Check` test, or the check loosened to let a violation pass | EF Core, Model | `issue (blocking)` |
| Query in a loop; `Include` missing where navigation is read | EF Core | `issue` |
| `DateTimeOffset` with non-zero offset saved on PostgreSQL | EF Core | `issue (blocking)` |
| `decimal` property named like a money amount without `HasPrecision(19, 4)` | EF Core, Model | `issue` |
| `DeleteBehavior.Cascade` or `ClientCascade`; `ON UPDATE CASCADE` in a migration | EF Core, Model | `issue (blocking)` |
| Composite or non-`Guid` primary key, or a key column not named `Id` | EF Core, Model | `issue` |
| Plural table name; `HasDefaultSchema` missing or the schema not confirmed | EF Core, Model | `issue` |
| Migration with `DropColumn`, `DropTable`, `RenameColumn`, or a narrowing `AlterColumn` | EF Core, Migrations | `issue (blocking)` — breaks the release still running |
| `AddColumn` with `nullable: false` or `defaultValue:` on an existing table | EF Core, Migrations | `issue (blocking)` |
| `CreateIndex` on an existing table without the online option and a confirmed row count | EF Core, Migrations | `issue` |
| Index change generated as `DropIndex` + `CreateIndex` | EF Core, Migrations | `issue` — `DROP_EXISTING` or create-then-drop |
| Migration or `ModelSnapshot` edited by hand; real connection string in a design-time factory | EF Core, Migrations | `issue (blocking)` |
| `Database.Migrate()` at startup in a service that can run more than one instance | EF Core, Migrations | `issue` |
| Scoped service injected into a singleton (captive dependency) | Design | `issue (blocking)` |
| `IDisposable` created and not disposed / not `await using` | Design | `issue` |
| Pattern (Strategy, Factory, Mediator) with one implementation | Design | `question` |
| MediatR added | Design | `issue (blocking)` — ConduitR |
| Workaround, ordering constraint, lock, or arbitrary-looking literal with no comment saying why | Comments | `issue` |
| Comment that contradicts the code beside it | Comments | `issue` — a stale claim misleads with authority |
| Public type or member without a `<summary>`, or one that only restates the name | Comments | `issue` |
| Comment narrating what the code plainly does | Comments | `nitpick` — improve the name instead |
| Commented-out code, change history, author tag, or task narration ("added per request") | Comments | `issue` |

Tests:

| Smell | Rule | Label |
|---|---|---|
| `A.Fake<T>()` without `.Strict()` | Strict fakes | `issue` |
| Asserting on an AutoFixture value that was never set | Assert what you set | `issue` |
| `Thread.Sleep`, `Task.Delay`, real clock | Independent and deterministic | `issue (blocking)` |
| Test name not `Method_WhenCondition_ShouldResult` (or the Given form) | Naming | `nitpick` |
| No `// Arrange` / `// Act` / `// Assert` | Structure | `nitpick` |
| Private method tested via reflection | Public behaviour only | `issue` |
| Second test for the same behaviour | No duplicates | `suggestion` |
| Test project with its own `PackageReference`s | `Tests.props` | `issue` |

Project and packages:

| Smell | Rule | Label |
|---|---|---|
| `Version="…"` on a `PackageReference` | central package management | `issue (blocking)` — fails restore |
| New package not in `allowed-licenses.json` tiers | [`dependencies.md`](../../dependencies.md) | `issue (blocking)` until reviewed |
| Package added without `nuget-license -t` passing on the whole graph | [`dependencies.md`](../../dependencies.md) | `issue (blocking)` |
| `Directory.Build.props` or `Directory.Packages.props` below the root | layout | `issue (blocking)` — severs the subtree |
| `.csproj` in `services/`, `jobs/`, `tools/` without `IsPackable=false` | layout | `todo` |
| A root-level solution in a monorepo | layout | `issue` |
