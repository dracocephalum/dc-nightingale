# Entity Framework Core rules

Apply to every project that owns a `DbContext`, an entity, or a migration.
The general rules in [csharp-coding-rules.md](csharp-coding-rules.md) still
apply; this file adds what is specific to EF Core, and it is read only when a
task touches it.

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
- **Same on the wire.** Register `JsonStringEnumConverter` globally so HTTP
  payloads carry names, not numbers. Put `[EnumDataType(typeof(T))]` on
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

        public static void Check(IModel model)
        {
            var violations = new List<string>();
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

                foreach (var fk in entity.GetForeignKeys().Where(fk => !fk.IsOwnership && fk.DeleteBehavior is not (DeleteBehavior.Restrict or DeleteBehavior.NoAction)))
                {
                    violations.Add($"{name} -> {fk.PrincipalEntityType.DisplayName()}: {fk.DeleteBehavior} - .OnDelete(DeleteBehavior.Restrict)");
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
                }
            }

            if (violations.Count > 0)
            {
                throw new InvalidOperationException("Model conventions violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
            }
        }

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

        // Act + Assert - throws with every violation listed
        ModelConventions.Check(context.Model);
    }

Extend the check when a rule is added here; never loosen it to make a
violation pass — the fix is the configuration line it names.

## Migrations

Every migration runs against a live database while the **previous release is
still serving**. The generator does not know that, so generating is step one
and editing the generated file against these rules is step two.

- **Generate, never hand-write.** `dotnet ef migrations add <Name>`. Add
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
