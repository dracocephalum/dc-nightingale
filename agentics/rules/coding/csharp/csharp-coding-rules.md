# C# coding rules

Apply to every C# project in this repository, tests included.

Formatting is enforced by the build — root `.editorconfig`, `stylecop.ruleset`,
warnings as errors — so it is not repeated here. This file covers what the
build cannot check.

## Observability

**Never emit PII, secrets, tokens, or payloads** in logs, metrics, or spans.
No exceptions.

Logging:

- Structured only: `ILogger` message templates with named placeholders. Never
  interpolated or concatenated strings.
- Production minimum level is **Warning**. `Information` is enabled only for
  specific classes or namespaces through configuration overrides — never as the
  default.
- Before adding a log line confirm it helps on-call or operations, is not
  already covered by a metric or span, is off the hot path, and carries no
  sensitive data. If unsure, leave it out.

Metrics and spans:

- No high-cardinality tags. Never `userId`, `sessionId`, `batchId`, `jobId`,
  request ids, or any unbounded value.
- Span attributes are few, stable, and non-PII.

## Design

- **Secure by default.** Least privilege, safe defaults, validate at boundaries.
- **Resilient.** A timeout on every external call. Retries only for idempotent
  operations. Explicit fallbacks.
- **Testable.** Retry, polling, and time-dependent logic take a `TimeProvider`
  and overridable timeouts, so tests never wait on real time.
- **Extensible without speculation.** Extract shared behaviour on the third
  concrete use — not "just in case". Prefer composition and constructor
  injection. Keep abstractions small and domain-shaped.
- Apply a pattern (Strategy, Factory, Decorator, Mediator) only when it buys
  clarity, testability, or extensibility. Simplest solution first.
- **Mediator via a library means ConduitR** (`ConduitR` +
  `ConduitR.DependencyInjection`; `ConduitR.Validation.FluentValidation` when
  validating). MIT-licensed. **Not MediatR**, which is commercially licensed
  from v13. The abstractions match MediatR's by name — `IRequest<T>`,
  `IRequestHandler<,>`, `INotification`, `IPipelineBehavior<,>` — but
  registration is `AddConduit(...)` and optional features are separate
  packages, so treat a migration as a port, not a package swap.
- **Never expose the persistence schema.** Queries return DTOs or projections;
  entities stay behind the boundary.
- **Expected failures are values or well-known exceptions; unexpected ones are
  exceptions.** Validation, not-found, conflict, business-rule rejections: either
  return a domain-specific result record carrying an error code, or throw a
  well-known domain exception that the pipeline translates — in ASP.NET Core, an
  `IExceptionHandler` producing `ProblemDetails`. Pick one style per component
  and keep to it. Everything else throws a specific domain type such as
  `ServerConnectionTimeoutException`, never bare `Exception`.

## Naming

- **`_camelCase` for private and internal fields**, PascalCase for everything
  a type exposes, camelCase for locals and parameters — the `dotnet/runtime`
  convention. The underscore is what makes a field recognizable at the point
  of use, which is why **`this.` is not written**: a parameter `value` cannot
  collide with a property `Value`, a field is already marked, and the compiler
  catches the rest. `stylecop.ruleset` has both rules off on purpose and says
  what turning either on would cost.

## One type per file, and the feature file

**One top-level type per file, named for it.** `SA1402` and `SA1649` enforce
both halves, so the name on disk is the name in code and nothing ever needs a
search. That is deterministic organization, and it is worth more to an agent
than to a person: a file is found by the type it is asked about.

The pattern that seems to fight it is CQRS — a request, its handler, its
result, and often its validator belong together, and splitting them across
four files is what makes a feature hard to hold in one read. They stay
together **as nested types under one static class named for the feature**,
which satisfies the rule because nested types are not counted:

    public static class GetTodayWeather
    {
        public sealed record Query(CityId City);
        public sealed record Result(Temperature Now, Forecast Next);
        public sealed class Validator : AbstractValidator<Query> { ... }
        public sealed class Handler(IWeatherSource source) : IRequestHandler<Query, Result> { ... }
    }

- **The outer class is a verb phrase**: `GetTodayWeather`, `SetLocation`,
  `CancelOrder`. It is the file name, and it is what the feature is called
  everywhere — in a ticket, a pull-request title, a log line.
- **The nested names are fixed**: `Query` for a read, `Command` for a write,
  then `Result`, `Handler`, and `Validator` when there is one. Never
  `GetTodayWeatherQuery` inside `GetTodayWeather`; the outer name already says
  it, and `GetTodayWeather.Query` reads as a sentence.
- **Request and result are records**, per *Types and APIs*. The handler is a
  sealed class and takes its dependencies through the primary constructor.
- Registration by assembly scanning finds nested types; nothing extra is
  needed for ConduitR to see them.

The second case is a settings tree: an options class owns the types of its
sub-sections as nested classes — *Configuration*, below.

## Types and APIs

- **Records for DTOs, messages, and events.** `init`, not `set`.
- **Typed IDs and value objects are `readonly record struct`.** Validate in
  the constructor and trust the value everywhere after. **No implicit conversion
  operators** — a `UserId` must never silently accept a `Guid`; convert
  explicitly at the boundary.
- **Accept the narrowest collection abstraction** — `IEnumerable<T>` to iterate,
  `IReadOnlyList<T>` to index. **Never return `List<T>` or `T[]` from a public
  API**; return `IReadOnlyList<T>` or `IReadOnlyCollection<T>`.
- **Stream with `IAsyncEnumerable<T>`** whenever the producer is genuinely
  streaming — unbounded, paged, or I/O-driven. Mark the producer's token
  `[EnumeratorCancellation]` and consume with `.WithCancellation(token)`.
  Bounded results materialize to `IReadOnlyList<T>`; never wrap a small
  in-memory list in `IAsyncEnumerable<T>`.
- **Map explicitly.** Extension methods such as `entity.ToDto()`. No
  reflection-based mappers.
- **Every `async` method takes a `CancellationToken`** and passes it on.
  **Never block on a task** — no `.Result`, `.Wait()`, or
  `.GetAwaiter().GetResult()`. `ConfigureAwait(false)` in `libraries/` only.
- **Never `!` a nullable warning away.** Fix the flow or make the type nullable.

## Time

- **`DateTimeOffset`, never `DateTime`.** No new `DateTime` properties, fields,
  parameters, or return types. Where a `DateTime` arrives — framework APIs,
  legacy code, a database — convert at that boundary and pass on a
  `DateTimeOffset`.
- **Conversion depends on `Kind`, and `Unspecified` is a stop.**
  `Utc` → `new DateTimeOffset(value, TimeSpan.Zero)`. `Local` →
  `new DateTimeOffset(value)`. `Unspecified` → the offset is unknowable:
  **ask the user** which zone the value is in. Never let
  `new DateTimeOffset(unspecified)` run — it silently assumes local time.
- **In production code, "now" is `TimeProvider.GetUtcNow()`.** Never
  `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, or
  `DateTimeOffset.UtcNow` there — the testability rule already requires the
  `TimeProvider`. Tests may use `DateTimeOffset.UtcNow` for arbitrary
  timestamps in test data; anything the system under test *reads* still goes
  through `FakeTimeProvider`. Enforced at build time by `BannedSymbols.txt`
  (`RS0030`) in non-test projects.

## Entity Framework Core

For entities, `DbContext` configuration, and migrations — enum storage,
`DateTimeOffset` on PostgreSQL, and the migration safety rules — follow
[csharp-ef-core-rules.md](csharp-ef-core-rules.md).

## Comments

Comments are for the next reader, who is a person — a maintainer, a reviewer,
the author a year on. Write prose: full sentences, plain words, as the design
would be explained across a desk. The measure is cognitive load: the reader
should not have to reconstruct the reasoning from the code, or open three
other files to learn why this one exists.

- **Why, not what.** The code says what. The comment says why this way: the
  requirement, the constraint, the trade-off, the alternative rejected.
  `// increment the counter` is noise; `// Counted before the write, so a
  crash between the two over-reports rather than under-reports; the
  reconciler tolerates that direction.` is a comment.
- **Every non-trivial type opens with its role**, in its `<summary>`: what it
  is for, what calls it, what it relies on, what it must never do — enough to
  decide without reading the body.
- **Rationale sits where the question arises.** A surprising line, a
  workaround, an ordering that matters, a lock, an arbitrary-looking literal:
  the comment goes directly above it. Link the ticket, spec, or vendor page
  when one exists; a link stays authoritative where a paraphrase drifts.
- **State invariants the type system does not enforce.** "Callers hold the
  lock", "sorted by time, oldest first", "never called concurrently".
- **A comment is a claim the code must keep true.** Change it in the same
  commit, or delete it; a stale comment misleads with authority, and
  reviewers read each one against the code beside it.
- **If a comment explains what the code does, fix the code first** — a better
  name, an extracted method — and comment what remains.
- **Nothing source control holds, nothing about the authoring session.** No
  commented-out code, change history, or author tags; no "added per request"
  or restatement of the rule that prompted the change. The reader was not in
  the conversation.
- **XML doc comments on every public API.** `<summary>` in one to three
  sentences; `<remarks>` for the design; `<example>`/`<code>` where a usage
  example clarifies. The build emits `$(AssemblyName).xml` beside each
  non-test assembly for IntelliSense. Missing comments do not fail the build
  — SA1600 and CS1591 are off — reviewers enforce.

## Conventions

- File-scoped namespaces. One `using` per line.
- `nameof` for member names, never string literals.
- Pattern matching and `switch` expressions where they read more clearly than
  `if`/`else` chains — not as a reflex.

### Configuration

- A component binds its settings from one configuration section named after
  it, into one options class, and reads nothing else from configuration.
- **The options class is the whole settings tree.** A sub-section's type is
  nested under the property that binds it — `ThingOptions.StoreSettings`,
  and `ThingOptions.StoreSettings.PartitioningMode` for an enum only that
  sub-section uses — as deep as the tree goes. One file then holds every
  setting, its default and its reason, and a nested name never repeats the
  outer one. A type used by more than one options class is not nested.
- Connection strings live under `ConnectionStrings`, where every .NET host and
  tool expects them, never inside the section. The section carries a
  `ConnectionStringName` setting, resolved with `GetConnectionString(name)`,
  with the component's own name as the default; that is what the reference
  libraries do and what an operator looks for.
- A setting that shapes something created once — a schema, a store, a queue —
  is recorded where it was applied and compared on every later start, so a
  changed setting is refused rather than silently disagreeing with what
  exists.

### gRPC contracts

- The proto package is the component's namespace plus `Protocol.V1`, and the
  version is part of the package from the first draft: renaming a package
  later changes every generated type.
- Services are named by area — `Streams`, `Subscriptions` — never after the
  product, which clashes with the namespace, and never after a domain type,
  which clashes with the type. Enums and messages that mirror a domain type
  get a wire-specific name (`ReadDirection`, `StreamBounds`).
- One folder holds every `.proto`, split by area the way the reference
  protocol is; the `Protobuf` item sets `ProtoRoot` to that folder so the
  files can import each other, and a file that holds only messages sets
  `GrpcServices="None"`, or its empty generated file fails `SA1518`.
- Errors cross the wire as `google.rpc.Status` with an `ErrorInfo` detail
  whose reason is an enum in the contract; the client maps reasons to the
  shared exceptions and leaves unknown reasons as the transport exception.

## Testing

For writing and validating unit tests — stack, naming, fakes, in-memory
database — follow [csharp-unit-tests-rules.md](csharp-unit-tests-rules.md).
