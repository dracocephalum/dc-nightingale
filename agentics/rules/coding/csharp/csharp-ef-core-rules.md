# Entity Framework Core rules

Apply to every project that owns a `DbContext`, an entity, or a migration.
The general rules in [csharp-coding-rules.md](csharp-coding-rules.md) still
apply; this file adds what is specific to EF Core, and it is read only when a
task touches it.

## Where it lives

A context, its entities and its migrations live in a folder and namespace
called `Data` in the project that owns them: `Data/AppDbContext.cs`, the
entities beside it, and the migrations under `Data/Migrations`. Not
`Persistence`, `Storage` or `Infrastructure`: `Data` is what the .NET
templates use, and one name across repositories means nobody looks twice.
The tool puts migrations in `Migrations` at the project root unless told
otherwise, so say where:

    dotnet ef migrations add <Name> --output-dir Data/Migrations

A context that mirrors another owner's tables lives in `Data` too; it has no
migrations.

## A schema another tool owns

Everything below assumes the context owns its tables: it names them, keys
them and migrates them. When something else creates and migrates the tables —
a store's own schema manager, a database that predates the repository, a
vendor's product — the context is a **mirror**, and with the user's agreement,
recorded in `DESIGN.md` with the reason, three things change:

- **Names, keys and column types follow the owner exactly.** Its table names,
  plural or prefixed as they are; its keys, natural or composite; the store
  type of every column, written out with `HasColumnType`. The naming and key
  rules under *Model* do not apply, and `ModelConventions.Check` is not run
  for that context.
- **No EF migrations for those tables.** The owner migrates them. A migration
  generated from the mirror would fight the owner for the same table.
- **A test holds the mirror to the owner.** It compares the EF model with the
  owner's table definitions — every table, and for each column its name,
  store type and nullability, and the primary key columns in order — and
  fails naming every difference. It takes the place of the conventions test
  and is as mandatory: nothing else compares the two, so without it a column
  the owner adds or retypes is found in production.

How the tables are shaped is no longer this file's to decide, so the rest of
*Model* and all of *Migrations* do not apply to a mirror either. A context
that owns some tables and mirrors others is two contexts.

## Model

Conventions are **checked, not patched**. Every rule below is configured
explicitly, per entity or per property, in `OnModelCreating`; a check at the
end of this section throws with the complete list of violations, so a
forgotten line fails the build instead of being silently rewritten by a
loop. The fix is always the explicit configuration the message names.

- **Schema: confirm it with the user; the default is the context's name.**
  Tables live in a schema named after the `DbContext` without the suffix —
  `TenantsDbContext` → `Tenants` — set once with
  `modelBuilder.HasDefaultSchema("Tenants")`. The migrations history table
  moves with it, `o => o.MigrationsHistoryTable("__EFMigrationsHistory", "Tenants")`
  on the provider options, so two contexts in one database never share a
  history. Ask before the first migration; moving tables between schemas
  later is a migration of every table.
- **Table name is the entity class name, singular.** `.ToTable("Order")` on
  every entity. EF's default is the `DbSet` property name, which is usually
  plural.
- **Every table has a set, named for its entity.** Each entity the context
  maps is exposed through a `DbSet` property, and the property is the class
  name or its plural where it has one: `Orders` for `Order`,
  `SequencerProgress` for `SequencerProgress`. Not a looser word for what the
  set holds: `Parked` for `SubscriptionParkedEvent` gives one table two
  names, depending on where it is read. A table with no set is reachable
  only through `Set<T>()`, which a reader of the context does not see. This
  rule applies to a mirror as well.
- **One primary key per table: a `Guid` column called `Id`.** Let EF generate
  the value — the SQL Server provider makes sequential GUIDs client-side; on
  other providers use `Guid.CreateVersion7()` so the clustered index does not
  fragment. **An explicit value wins**: set `Id` before `Add` and EF keeps
  it, the generator only fills `Guid.Empty` — so an entity keyed by an
  external identifier, an event id in an event-sourced system, sets it and
  nothing else changes. The typed-ID rule still applies: `readonly record
  struct OrderId(Guid Value)` with a value converter, and the column is still
  `Id`. A natural key is a unique index, never the primary key. A composite
  key is only for an explicit join entity, and only with the user's agreement.
- **No cascades, ever.** `.OnDelete(DeleteBehavior.Restrict)` on every
  relationship (`NoAction` where SQL Server rejects a cycle) — EF's default
  for a required relationship is `Cascade`. Deleting a parent with children
  is an explicit decision in code, never a side effect of the database, and
  `ClientCascade` counts as a cascade. Cascade *update* never arises because
  keys are immutable: never update a primary key, and never write
  `ON UPDATE CASCADE` in a migration. Owned types are the one exception EF
  requires.
- **A column that refers to another table is named for it: `<Table>Id`.**
  `SubscriptionGroupId` for a row of `SubscriptionGroup`, never `GroupId` or
  `ParentId`: a reference can then be read off a column name, by a person and
  by generated SQL alike. **When the table is this context's own, the column
  is a foreign key to it**, declared and with no cascade, which also gives it
  an index — EF adds one for a foreign key unless an index that leads with
  the column already exists; SQL Server would not on its own. **When the
  table is another owner's, the column keeps the name and takes no
  constraint**: a context does not alter a schema it does not own, the
  owner's column may not be unique, and the owner may delete rows whatever
  refers to them. Such a column is passed to the check by name, so the
  exception is stated rather than silent, and a join to the owner's table
  goes through a column the owner indexes. Anything else needs the user's
  agreement.
- **Money has an explicit precision; fiat is `decimal(19, 4)`.** A `decimal`
  property whose name says currency amount — `Amount`, `Price`, `Total`,
  `Cost`, `Fee`, `Balance`, and any `*Amount` — is `HasPrecision(19, 4)`:
  four decimal places is the fiat convention, and rounding to two happens at
  display. EF's default is `decimal(18, 2)`, which silently rounds on save.
  Never the SQL Server `money` type. **Crypto is not fiat**: if the domain
  holds crypto amounts, ask the user which precision — 8 places for
  BTC-style, 18 for ETH-style tokens, `decimal(38, 18)` is a common choice —
  the check only insists that one was chosen. Rates, quantities, and
  percentages are not money; give them their own precision.
- **Enums are stored as bounded strings, never numbers.**
  `.HasConversion<string>().HasMaxLength(64)` on every enum property — longer
  for an enum whose member names exceed 64 characters. Without the conversion
  the column is `int`; without the length it is `nvarchar(max)`.
- **Same on the wire.** Enums are names in JSON too, through the repository's
  one set of serializer options; see *JSON* in
  [csharp-coding-rules.md](csharp-coding-rules.md). Put `[EnumDataType(typeof(T))]` on
  **request DTO** enum properties — it rejects undefined values such as `999`
  during model validation. It has no effect on EF; do not put it on entities
  for that purpose.
- **`DateTimeOffset` on PostgreSQL:** Npgsql accepts only offset-zero values
  for `timestamp with time zone`. Normalise with `.ToUniversalTime()` before
  saving.

**The check.** Lives beside the `DbContext`, runs from a test — building the
model needs the provider's type mappings, not a connection, so the
design-time placeholder string is enough. Verified against EF Core 10.0.11
with the SQL Server provider: typed IDs, string enums, and `Restrict`
relationships pass; a defaulted entity fails on every line at once.

    public static class ModelConventions
    {
        private static readonly string[] MoneySuffixes = ["Amount", "Price", "Total", "Cost", "Fee", "Balance"];

        public static void Check(IModel model, params string[] externalReferences)
        {
            var violations = new List<string>();
            var tables = model.GetEntityTypes().Where(e => !e.IsOwned()).Select(e => e.GetTableName()).ToHashSet(StringComparer.Ordinal);
            foreach (var entity in model.GetEntityTypes().Where(e => !e.IsOwned()))
            {
                var name = entity.DisplayName();
                if (entity.BaseType is null)
                {
                    if (entity.GetTableName() != entity.ClrType.Name)
                    {
                        violations.Add($"{name}: table must be '{entity.ClrType.Name}' - .ToTable(\"{entity.ClrType.Name}\")");
                    }

                    var key = entity.FindPrimaryKey();
                    if (key is null || key.Properties.Count != 1 || key.Properties[0].Name != "Id" || StoreType(key.Properties[0]) != typeof(Guid))
                    {
                        violations.Add($"{name}: primary key must be a single Guid column named Id");
                    }
                }

                foreach (var fk in entity.GetForeignKeys().Where(fk => !fk.IsOwnership))
                {
                    var principal = fk.PrincipalEntityType.GetTableName();
                    if (fk.DeleteBehavior is not (DeleteBehavior.Restrict or DeleteBehavior.NoAction))
                    {
                        violations.Add($"{name} -> {fk.PrincipalEntityType.DisplayName()}: {fk.DeleteBehavior} - .OnDelete(DeleteBehavior.Restrict)");
                    }

                    if (fk.Properties.Count != 1 || fk.Properties[0].Name != principal + "Id")
                    {
                        violations.Add($"{name} -> {fk.PrincipalEntityType.DisplayName()}: the foreign key column must be '{principal}Id', the table it refers to and Id");
                    }
                }

                foreach (var property in entity.GetProperties())
                {
                    var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                    if (clr.IsEnum && (StoreType(property) != typeof(string) || property.GetMaxLength() is null))
                    {
                        violations.Add($"{name}.{property.Name}: enum must be a bounded string - .HasConversion<string>().HasMaxLength(64)");
                    }

                    if (clr == typeof(decimal) && MoneySuffixes.Any(s => property.Name.EndsWith(s, StringComparison.Ordinal)) && property.GetPrecision() is null)
                    {
                        violations.Add($"{name}.{property.Name}: money needs an explicit precision - .HasPrecision(19, 4), or the agreed crypto precision");
                    }

                    // A Guid column named <Something>Id says it refers to a row of <Something>:
                    // a table of this model, and then a foreign key to it, or another owner's
                    // table, and then listed as an external reference.
                    if (clr == typeof(Guid) && property.Name != "Id" && property.Name.EndsWith("Id", StringComparison.Ordinal) && !property.IsForeignKey())
                    {
                        var referred = property.Name[..^2];
                        if (tables.Contains(referred))
                        {
                            violations.Add($"{name}.{property.Name}: refers to {referred} by name and is not a foreign key to it - .HasOne<{referred}>().WithMany().HasForeignKey(...).OnDelete(DeleteBehavior.Restrict)");
                        }
                        else if (Array.IndexOf(externalReferences, $"{entity.ClrType.Name}.{property.Name}") < 0)
                        {
                            violations.Add($"{name}.{property.Name}: named like a reference to a table '{referred}' this model does not have - name it for the table it refers to, or list it as an external reference");
                        }
                    }
                }
            }

            if (violations.Count > 0)
            {
                throw new InvalidOperationException("Model conventions violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
            }
        }

        public static void CheckSets(DbContext context)
        {
            var violations = new List<string>();
            var sets = context.GetType().GetProperties()
                .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .ToList();
            foreach (var entity in context.Model.GetEntityTypes().Where(e => !e.IsOwned()))
            {
                var name = entity.ClrType.Name;
                var own = sets.Where(s => s.PropertyType.GetGenericArguments()[0] == entity.ClrType).ToList();
                if (own.Count == 0)
                {
                    violations.Add($"{name}: the context has no DbSet<{name}> - every table it maps is exposed, as '{name}' or its plural");
                }

                foreach (var set in own.Where(s => !IsNameOrPlural(s.Name, name)))
                {
                    violations.Add($"{context.GetType().Name}.{set.Name}: a set is named for its entity - '{name}' or its plural");
                }
            }

            if (violations.Count > 0)
            {
                throw new InvalidOperationException("Model conventions violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
            }
        }

        private static bool IsNameOrPlural(string candidate, string name) =>
            candidate == name
            || candidate == name + "s"
            || candidate == name + "es"
            || (name.EndsWith('y') && candidate == name[..^1] + "ies");

        private static Type StoreType(IProperty property) => property.GetTypeMapping().Converter?.ProviderClrType ?? property.ClrType;
    }

The test, one per `DbContext`:

    [Fact]
    public void Model_WhenBuilt_ShouldFollowConventions()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=design-time;Integrated Security=true")
            .Options;
        using var context = new AppDbContext(options);

        // Act + Assert - throws with every violation listed. A column that refers to a table
        // this context does not own is named here: ModelConventions.Check(context.Model, "Order.EventId")
        ModelConventions.Check(context.Model);
        ModelConventions.CheckSets(context);
    }

Extend the check when a rule is added here; never loosen it to make a
violation pass — the fix is the configuration line it names.

## Migrations

Every migration runs against a live database while the **previous release is
still serving**. The generator does not know that, so generating is step one
and editing the generated file against these rules is step two.

- **Generate, never hand-write.** `dotnet ef migrations add <Name> --output-dir Data/Migrations`. Add
  `dotnet-ef` to `.config/dotnet-tools.json` as a local tool when the first
  `DbContext` arrives — it does not ship there by default — and pin it to the
  same version as `Microsoft.EntityFrameworkCore.Design`. A hand-written
  migration or a
  hand-edited `ModelSnapshot` desynchronises the snapshot, and every later
  migration is generated from the wrong model. If the `DbContext` cannot be
  constructed at design time (it needs configuration or DI), add an
  `IDesignTimeDbContextFactory<T>` with a **placeholder** connection string —
  EF never opens it, and a real one has no business in source:

      public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
      {
          public AppDbContext CreateDbContext(string[] args) =>
              new(new DbContextOptionsBuilder<AppDbContext>()
                  .UseSqlServer("Server=localhost;Database=design-time;Integrated Security=true")
                  .Options);
      }

- **The migrations folder is generated code to the analyzers.** The generator
  writes what the ruleset refuses under warnings-as-errors — initializers
  without trailing commas, constant array arguments, using directives run
  together — and its output is not held to a style it did not choose. The
  shipped `.editorconfig` marks `**/Migrations/*.cs` as generated, so a first
  migration builds. The rules in this section still apply to what the file
  *does*; that is the edit of step two, and it is about safety, not style.
- **Before the first release, one migration.** Until a database exists that
  must be migrated rather than re-created, every model change would add a
  migration only ever applied to empty databases. So until the first official
  release there is a single migration, regenerated on each change: delete
  the migrations folder and add it again under the same name. The first
  release freezes it, and from then every change is a migration of its own
  under the rules below. **The condition is the whole rule**: the moment one
  database that matters has the migration applied, it is frozen, release or
  not. Record the choice where the migrations are documented.
- **Backward compatible with the code in production.** Deploy order is
  migrate first, then roll out; during the rollout old and new code share the
  schema. So a migration must never break the previous release: no rename, no
  drop, no narrowing, no new `NOT NULL` column without a value old code will
  supply. Change in two phases across releases — **expand** now (add the new
  thing, keep the old), **contract** later (remove the old, once no running
  code touches it). Renaming a column is the classic outage: the old code is
  still running, and its next query fails.
- **Never drop a column in the change that stops using it.** Leave it; a
  separate migration, a release later, drops it once production has run
  without it. An unused column costs nothing; a dropped one that something
  still read is an outage.
- **New columns on existing tables are nullable.** Old code will not set the
  column, and adding a column with a default value rewrites and locks the
  table on SQL Server Standard and on PostgreSQL before 11 (or with a volatile
  default such as `now()` on any version) — metadata-only on the other tiers
  is not something to rely on across environments. A column that must end up
  required: add it nullable, backfill in batches (below), then a later
  migration adds the constraint — itself a full scan, so on a large table it
  goes through the manual route described under indexes. On a **new** table
  any nullability is fine; no old code knows the table.
- **Changing a column's type or length.** Widening `varchar`/`nvarchar` —
  not to `max`/`text`, not narrowing, not changing the type — is metadata-only
  on SQL Server and PostgreSQL, so a plain `AlterColumn` is acceptable.
  Anything else rewrites the table under a schema lock, and is done as
  expand / copy / swap / contract:

  1. **Expand** — migration 1: `AddColumn` `<name>_new` with the target type,
     nullable; recreate any index the old column has on the new one (online,
     see below).
  2. **Copy** — in batches of 4999, under SQL Server's lock-escalation
     threshold of 5,000 locks, so no batch takes the whole table. Idempotent,
     so it can be rerun:

         WHILE 1 = 1
         BEGIN
             UPDATE TOP (4999) t SET t.[col_new] = CAST(t.[col] AS <target type>)
             FROM [Table] t
             WHERE t.[col] IS NOT NULL AND t.[col_new] IS NULL;
             IF @@ROWCOUNT = 0 BREAK;
         END

     Where it runs is a **confirmation with the user, on the row count**.
     A small table: the loop goes in `migrationBuilder.Sql(...)` in migration
     1 — inside the migration's transaction the batches only avoid
     lock escalation, they do not shorten the transaction, and a table that
     takes minutes to copy hits the command timeout and rolls the whole
     migration back. A large table: comment the `Sql(...)` call out, leave the
     loop in a `/* */` block with the ticket number, and the operator runs it
     by hand — the same hand-over as for indexes below.
  3. **Swap** — migration 2: in the same deployment for a small table, in the
     **next** release for a large one (the copy must have finished). A
     catch-up copy for rows changed since —
     the same `UPDATE` without `TOP`, comparing `col_new` to `CAST(col)` — then
     `RenameColumn` `col` → `col_old` and `col_new` → `col`. Both renames are
     in the one migration, so one transaction: the name is never absent. Views,
     computed columns, and procedures referencing the column by name are
     recreated in the same migration. The entity keeps its property and
     `HasColumnName`; nothing in code changes.
  4. **Contract** — migration 3, a release later: `DropColumn` `col_old`,
     with its indexes.

  A column that is a key or a foreign key is outside this procedure — stop
  and design the change with the user. So is a table hot enough that the
  catch-up in step 3 is itself large; that needs dual writes from code during
  the transition.
- **Indexes on existing tables: online, and ask about the volume first.**
  Generated `CreateIndex` builds offline, holding a schema lock for the
  duration; a changed index is generated as `DropIndex` + `CreateIndex`,
  leaving the table unindexed in between. Before adding or changing an index
  on an existing table, **ask the user for the expected row count and the
  database SKU**, then:
  - Configure it online in the model, so the generated migration is right on
    a small table: SQL Server `.IsCreatedOnline()` → `WITH (ONLINE = ON)`
    (Enterprise and Azure SQL; Standard rejects it); PostgreSQL
    `.IsCreatedConcurrently()` → `CONCURRENTLY`, which cannot run inside a
    transaction — the migration needs `suppressTransaction: true` for that
    step.
  - If the table is large or the tier is low, **comment out** the
    `CreateIndex` / `DropIndex` calls and put the SQL to run in a `/* */`
    block above them, with the ticket number. The migration then applies as
    a no-op and the operator builds the index separately. The snapshot
    already records the index, so a later `migrations add` will not
    regenerate it — the comment is the only record that it is still owed.
  - An index **change** is one statement on SQL Server —
    `CREATE INDEX … WITH (DROP_EXISTING = ON, ONLINE = ON)`, the old index
    serving until the new one is ready. On PostgreSQL, or where online is not
    supported, it is two: create the new index under a new name, then drop
    the old — never drop first.
- **Apply from the pipeline, not from `Program.cs` — unless one instance is
  guaranteed.** `Database.Migrate()` at startup races when several instances
  start together, and breaks the migrate-then-roll-out order the
  compatibility rule depends on. The default is
  `dotnet ef migrations script --idempotent` run as a deployment step before
  the rollout. A service that is **guaranteed to run at most one instance** —
  a single-replica deployment, a global mutex, leader election — may migrate
  at startup; state that guarantee in a comment next to the call, because
  scaling it out later silently removes it.
