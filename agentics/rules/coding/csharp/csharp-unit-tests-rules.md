# C# unit tests

Applies to C# test projects only. The [coding rules](csharp-coding-rules.md)
apply to test code as well; this file adds only what is specific to tests.

## Where tests live

Layout, naming, and project wiring are defined in the repository's root
`AGENTS.md` — see *Repository layout*, *Conventions*, and
*Building and testing*. The short version:

- `test/<Project>.Tests/` mirrors `src/<Project>/`. The namespace drops a
  trailing `.Core`: `Contoso.Core.Tests` → `Contoso.Tests`.
- Every `*.Tests` project inherits the full test stack from `Tests.props`. A
  test `.csproj` holds a `TargetFramework` and a `ProjectReference` and
  **nothing else** — add no package references.
- A `*.Tests` project is **unit tests only**: nothing in it may need a live
  dependency, so `dotnet test` runs everywhere, always. Tests that need one
  live in `test/<Project>.Tests.Integration/` — see *Integration tests*.

## Stack

| Concern | Use |
|---|---|
| Framework | xUnit v3 — `[Fact]`, `[Theory]`; on Microsoft.Testing.Platform, so test projects are executables |
| Theory data | `[InlineData]`, `[MemberData]`, `[CombinatorialData]` |
| Assertions | Shouldly — `ShouldBe`, `ShouldNotBeNull`, `Should.Throw<T>` |
| Structural equality | DeepEqual — `ShouldDeepEqual` for mapping and DTO tests |
| Fakes | FakeItEasy, **strict** — `A.Fake<T>(o => o.Strict())` |
| Test data | AutoFixture |
| Time | `FakeTimeProvider` — never real clocks, `Thread.Sleep`, or `Task.Delay` |
| ASP.NET Core | `WebApplicationFactory<TEntryPoint>` |
| EF Core | in-memory provider, one database per test — see Rules |

## Workflow

1. **Scope.** Take the target from the request or the open file. Given a
   specific class, file, or scenario, cover only that. Otherwise cover the
   unit's public behaviour plus its important edge and failure cases.
2. **Write.** One test per behaviour. Arrange with the least setup that states
   the scenario. Act once. Assert on outcomes and on required dependency calls
   — `A.CallTo(...).MustHaveHappenedOnceExactly()`.
3. **Check it can fail.** Break the implementation, confirm the test fails for
   the right reason, restore.
4. **Run the suite.** All green, expected test count, no flakiness.
5. **Report gaps.** State which cases are not covered and why — external
   dependency, untestable design — rather than leaving them silent.

## Rules

- **Strict fakes.** Configure only the calls you expect; any other call fails
  the test. That is what turns a fake into a specification.
- **`sut`** names the system under test, whether field or local.
- **`// Arrange`, `// Act`, `// Assert`** in that order. `// Act & Assert` when
  the two are a single expression, as with `Should.Throw`.
- **Naming** — one of these, matching whatever the class already uses:
  - `Method_WhenCondition_ShouldResult` — `Cancel_WhenShipped_ShouldThrow`
  - `GivenCondition_WhenAction_ShouldResult` — `GivenShippedOrder_WhenCancel_ShouldThrow`
- **Nested classes only when a flat class has outgrown its names** — an
  aggregate with dozens of commands, each with happy-path, rejection, and
  idempotency tests. Then one nested class per member, and the member leaves
  the test name: `OrderTests.Cancel.WhenShipped_ShouldThrow`. Several hundred
  tests in one flat class is still fine; nest for grouping, not for size.
- **`[Theory]`** when several inputs exercise one behaviour; `[Fact]` otherwise.
- **Assert only what you set.** Never assert on an AutoFixture-generated value
  you did not fix with `.With(...)`. The exception is DeepEqual, when the test
  is about transforming the whole object.
- **Independent and deterministic.** No shared mutable state, no order
  dependence, no real time, I/O, network, or environment.
- **In-memory EF Core** is acceptable for query and read-model handlers where
  faking `DbSet`/`DbContext` is impractical. One database per test:
  `UseInMemoryDatabase(Guid.NewGuid().ToString())`. Seed with `SaveChanges()`,
  not `SaveChangesAsync()`, from a synchronous constructor.
- **Public behaviour only.** Do not test private methods.
- **No duplicates.** A second test for the same behaviour is maintenance
  without coverage.
- **No PII or high-cardinality values** in test names, data, or log output.
- **Never assert on a `Task` itself.** Shouldly's `ShouldNotBeNull()` on a
  `Task` binds to its async overload and returns one, so the call is never
  awaited and `CS4014` fails the build under warnings-as-errors. Await the
  task and assert on the result, or assert on a value such as
  `IsCompletedSuccessfully`.

## Integration tests

A unit test is preferred whenever one can state the behaviour; an integration
test earns its place when only a real dependency can — a database, a broker,
a server hosted for real. Those live in a sibling project named
`test/<Project>.Tests.Integration/`, which gets the same stack as a `*.Tests`
project and follows the same rules, with four differences:

- **A plain `dotnet test` runs none of them.** The project builds, so the
  tests are always compiled, but it is not a test project until the run asks:

      dotnet test <solution>.slnx -p:RunIntegrationTests=true

  An accidental `dotnet test` on a machine without the dependency therefore
  still passes, and the unit run stays fast.
- **They own what they touch.** A test creates the resource it needs under a
  recognisable name — a database with a fixed prefix and a random suffix — and
  removes it when the run ends, pass or fail. Where the dependency lives comes
  from an environment variable with a documented local default, never from a
  tracked file.
- **They are marked** `[Trait("Category", "Integration")]` on the class, so a
  report can tell the two kinds apart.
- **Tests that each create a database share one collection**, so they run
  one at a time. A test class runs in parallel with every other class by
  default, and a database server short of memory hands out one memory grant
  at a time for the catalog queries a schema apply runs; parallel creations
  then stall each other until the command timeout, which reads as a hang.

Until a pipeline runs them, automated integration tests are worth less than a
sample program that exercises the same path and can be read. A repository with
an `examples/` category keeps its end-to-end runs there, each sample with a
test that runs it, and puts only backend-level checks in a
`*.Tests.Integration` project.

## Running

Test projects are executables on Microsoft.Testing.Platform — xunit v3's home,
and on .NET 10 SDK the only one `dotnet test` supports. `global.json` opts the
command in; `Tests.props` sets the three properties per project.

    dotnet test path/to/Project.Tests.csproj
    dotnet test path/to/Project.Tests.csproj -- --filter-method "*.ClassName.*"
    dotnet run  --project path/to/Project.Tests.csproj     # the same tests, as a program

Arguments after `--` go to the platform, and the xunit filters are its own —
`--filter-class`, `--filter-method`, `--filter-namespace`, `--filter-trait` —
not the old `--filter` expression, which the platform does not accept.

Confirm the reported total matches the tests you expect — a misconfigured
runner reports zero tests and exits green, and under the platform the summary
reads `total:` rather than `Passed!`.

### Two things xunit v3 changes in the code

- **`IAsyncLifetime` uses `ValueTask`** — `ValueTask InitializeAsync()` and
  `ValueTask DisposeAsync()`. The v2 `Task` signatures do not compile.
- **Pass `TestContext.Current.CancellationToken`** to anything in a test that
  accepts a cancellation token. Analyzer `xUnit1051` requires it so a cancelled
  run stops promptly, and warnings-as-errors makes the omission fatal.

### Coverage

    dotnet test --solution <solution>.slnx -- --coverage --coverage-output-format cobertura --coverage-output TestResults/coverage.cobertura.xml --coverage-settings coverage.config

The collector is the platform's own, `Microsoft.Testing.Extensions.CodeCoverage`,
in every test project through `Tests.props`; `coverage.config` at the
repository root is its settings and is passed each time — it is not found on
its own. It excludes only code nobody writes by hand: test assemblies, anything
compiled from `obj/` (generated code, which carries no attribute the collector
would see), and the generated and excluded-member attributes. Measured: with
defaults the report counted the test assembly and the generated gRPC code;
with the file, only hand-written sources. Add an exclusion there for the same
reason only; a class that is hard to test is a finding about the class, not an
exclusion. Obsolete code is not excluded either: it is still in the build and
still runs, so it is measured until it is deleted.

Each run overwrites the output path you name; give it a path under
`TestResults/`, which is ignored, and read the line rate from the root element
rather than opening a viewer:

    grep -om1 'line-rate="[0-9.]*"' TestResults/coverage.cobertura.xml

Report the line rate against the two numbers under `coverage:` in
`.agentics.yaml`: below `minimum` is a failure and is said so plainly, at or
above `target` is green, and between the two is a warning. The collector
cannot enforce them — it cannot fail a run on a threshold — so the report is
where they bite until a pipeline reads the same numbers.
