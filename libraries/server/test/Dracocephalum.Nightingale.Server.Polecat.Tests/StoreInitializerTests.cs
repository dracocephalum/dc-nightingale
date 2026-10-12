using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// Every decision the initializer makes, against a database and a schema that only answer: what
/// each mode may do, when it creates, when it initializes, when it applies and when it refuses,
/// what it says, and that the schema lock is held, shared to verify and exclusive to change,
/// around everything it reads or changes. What the real database and the real schema do under
/// those decisions is the integration tests'.
/// </summary>
public sealed class StoreInitializerTests
{
    private const int Utf8 = 65001;
    private const int Latin1 = 1252;
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly FakeDatabase _database = new();
    private readonly FakeSchema _schema = new();
    private readonly NightingaleOptions _options = new();

    [Theory]
    [InlineData("")]
    [InlineData("master")]
    [InlineData("MASTER")]
    public async Task Initialize_WhenTheConnectionStringNamesNoDatabaseOfItsOwn_ShouldRefuse(string name)
    {
        // Arrange
        _database.Name = name;

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain("must name the database to use");
        _database.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(SchemaMode.Verify)]
    [InlineData(SchemaMode.Migrate)]
    public async Task Initialize_WhenTheDatabaseIsMissingAndTheModeDoesNotInitialize_ShouldRefuseAndCreateNothing(SchemaMode mode)
    {
        // Arrange
        _database.Exists = false;

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(mode));

        // Assert: the way out is named, and nothing was locked, let alone created.
        exception.Message.ShouldContain($"Nightingale:SchemaMode is {mode}");
        exception.Message.ShouldContain("--initialize");
        _database.Calls.ShouldBe(["exists"]);
        _schema.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Initialize_WhenTheDatabaseIsMissing_ShouldCreateItWithTheConfiguredCollationThenInitializeIt()
    {
        // Arrange
        _database.Exists = false;
        _database.Collation = new(NightingaleOptions.StoreOptions.DefaultCollation, Utf8, null, null);
        _options.Store.Partitioning = PartitioningMode.Tenant;
        _options.Store.AssignOrdinals = true;

        // Act
        await InitializeAsync();

        // Assert: the collation is checked before it is used, the lock is taken once the database
        // exists and held to the end, the store's tables come before the record of what shaped
        // them, and the record holds what is configured.
        _database.Calls.ShouldBe(["exists", $"code page of {NightingaleOptions.StoreOptions.DefaultCollation}", $"create {NightingaleOptions.StoreOptions.DefaultCollation}", "lock exclusive", "list tables", "collation", "unlock"]);
        _schema.Calls.ShouldBe(["report", "apply", "write initialization"]);
        var written = _schema.Initialization.ShouldNotBeNull();
        written.Settings.Schema.ShouldBe(_options.Store.Schema);
        written.Settings.Collation.ShouldBe(NightingaleOptions.StoreOptions.DefaultCollation);
        written.Settings.Partitioning.ShouldBe(PartitioningMode.Tenant);
        written.Settings.AssignOrdinals.ShouldBeTrue();
        written.CreatedAt.ShouldBe(Now);
        written.CreatedBy.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Initialize_WhenNoCollationIsConfigured_ShouldCreateWithTheServersDefaultAndRecordTheActualOne()
    {
        // Arrange
        _database.Exists = false;
        _database.Collation = new("Latin1_General_100_CI_AS_SC_UTF8", Utf8, null, null);
        _options.Store.Collation = null;

        // Act
        await InitializeAsync();

        // Assert
        _database.Calls.ShouldBe(["exists", "create (server default)", "lock exclusive", "list tables", "collation", "unlock"]);
        _schema.Initialization.ShouldNotBeNull().Settings.Collation.ShouldBe("Latin1_General_100_CI_AS_SC_UTF8");
    }

    [Fact]
    public async Task Initialize_WhenTheConfiguredCollationIsUnknown_ShouldRefuseBeforeCreatingAnything()
    {
        // Arrange
        _database.Exists = false;
        _database.CodePages["No_Such_Collation"] = null;
        _options.Store.Collation = "No_Such_Collation";

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain("is not a collation this SQL Server knows");
        _database.Calls.ShouldNotContain(call => call.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Initialize_WhenTheConfiguredCollationIsNotUtf8_ShouldRefuseBeforeCreatingAnythingAndNameTheWayOut()
    {
        // Arrange
        _database.Exists = false;
        _database.CodePages["Latin1_General_100_BIN2"] = Latin1;
        _options.Store.Collation = "Latin1_General_100_BIN2";

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain("is not a UTF-8 collation");
        exception.Message.ShouldContain("Nightingale:Store:IgnoreCollationCompatibility");
        _database.Calls.ShouldNotContain(call => call.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Initialize_WhenTheHostIgnoresCollationCompatibility_ShouldCreateAndInitializeUnderACollationThatIsNotUtf8()
    {
        // Arrange
        _database.Exists = false;
        _database.CodePages["Latin1_General_100_BIN2"] = Latin1;
        _database.Collation = new("Latin1_General_100_BIN2", Latin1, null, null);
        _options.Store.Collation = "Latin1_General_100_BIN2";
        _options.Store.IgnoreCollationCompatibility = true;

        // Act
        await InitializeAsync();

        // Assert
        _database.Calls.ShouldContain("create Latin1_General_100_BIN2");
        _schema.Initialization.ShouldNotBeNull().Settings.Collation.ShouldBe("Latin1_General_100_BIN2");
    }

    [Fact]
    public async Task Initialize_WhenAProvisionedDatabaseIsEmpty_ShouldInitializeItWithoutCreating()
    {
        // Arrange: a database administrator created the database; the login may not create one.

        // Act
        await InitializeAsync();

        // Assert
        _database.Calls.ShouldBe(["exists", "lock exclusive", "list tables", "collation", "unlock"]);
        _schema.Calls.ShouldBe(["report", "apply", "write initialization"]);
    }

    [Theory]
    [InlineData(SchemaMode.Verify, "lock shared")]
    [InlineData(SchemaMode.Migrate, "lock exclusive")]
    public async Task Initialize_WhenTheDatabaseIsNotInitializedAndTheModeDoesNotInitialize_ShouldRefuseUnderTheLockAndChangeNothing(SchemaMode mode, string lockTaken)
    {
        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(mode));

        // Assert
        exception.Message.ShouldContain("has not been initialized by Nightingale");
        exception.Message.ShouldContain($"Nightingale:SchemaMode is {mode}");
        exception.Message.ShouldContain("--initialize");
        _database.Calls.ShouldBe(["exists", lockTaken, "unlock"]);
        _schema.Calls.ShouldBe(["report"]);
    }

    [Fact]
    public async Task Initialize_WhenTheDatabaseHoldsATableThatIsNotTheServersAndNoRecordOfAStore_ShouldRefuse()
    {
        // Arrange: two of the server's own tables, empty, and one that is nobody's.
        _database.Tables.Add(new(new("dbo", "pc_events"), HasRows: false));
        _database.Tables.Add(new(new("dbo", "pc_streams"), HasRows: false));
        _database.Tables.Add(new(new("dbo", "somebody_elses"), HasRows: false));

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain("holds 3 tables");
        exception.Message.ShouldContain("not initialized by Nightingale");
        exception.Message.ShouldContain("dbo.somebody_elses are not tables this server creates");
        _schema.Calls.ShouldBe(["report", "own tables"]);
    }

    [Fact]
    public async Task Initialize_WhenTheDatabaseHoldsOnlyEmptyTablesOfTheServers_ShouldInitializeItTheRestOfTheWay()
    {
        // Arrange: what a start that died between the store's tables and the gateway's migrations
        // leaves, here with the migrations' history table made and nothing in it.
        _database.Tables.Add(new(new("dbo", "pc_events"), HasRows: false));
        _database.Tables.Add(new(new("nightingale", "__EFMigrationsHistory"), HasRows: false));

        // Act
        await InitializeAsync();

        // Assert
        _database.Calls.ShouldBe(["exists", "lock exclusive", "list tables", "collation", "unlock"]);
        _schema.Calls.ShouldBe(["report", "own tables", "apply", "write initialization"]);
    }

    [Fact]
    public async Task Initialize_WhenAnEarlierInitializationDiedBeforeTheRecordOfTheStore_ShouldFinishItWhenInitializingAndRefuseOtherwise()
    {
        // Arrange: the migrations are applied, so their history holds rows, and the record was
        // never written; the tables the record would go in are empty.
        _schema.Report = new SchemaReport(true, false, null, [], [], []) { InitializationUnfinished = true };
        _database.Tables.Add(new(new("dbo", "pc_events"), HasRows: false));
        _database.Tables.Add(new(new("nightingale", "__EFMigrationsHistory"), HasRows: true));
        _database.Tables.Add(new(new("nightingale", "StoreProperty"), HasRows: false));
        _schema.OwnTables.Add(new("nightingale", "StoreProperty"));

        // Act
        var verifying = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(SchemaMode.Verify));
        var migrating = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(SchemaMode.Migrate));
        await InitializeAsync(SchemaMode.Initialize);

        // Assert: the history's rows are what such an initialization leaves, and are not held
        // against it; the whole record is written, and the two modes that do not initialize say
        // what happened and what finishes it.
        verifying.Message.ShouldContain("did not finish");
        verifying.Message.ShouldContain("--initialize");
        migrating.Message.ShouldContain("did not finish");
        _schema.Calls.ShouldBe(["report", "report", "report", "own tables", "apply", "write initialization"]);
        _schema.Initialization.ShouldNotBeNull();
    }

    [Fact]
    public async Task Initialize_WhenATableOfTheServersHoldsRowsAndNoRecordOfAStore_ShouldRefuse()
    {
        // Arrange
        _database.Tables.Add(new(new("dbo", "pc_events"), HasRows: true));
        _database.Tables.Add(new(new("dbo", "pc_streams"), HasRows: false));

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain("dbo.pc_events hold rows");
        exception.Message.ShouldContain("only initializes an empty database");
        _schema.Calls.ShouldBe(["report", "own tables"]);
    }

    [Fact]
    public async Task Initialize_WhenAProvisionedDatabaseIsNotUtf8_ShouldRefuseAndInitializeNothing()
    {
        // Arrange
        _database.Collation = new("SQL_Latin1_General_CP1_CI_AS", Latin1, null, null);

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain("has collation SQL_Latin1_General_CP1_CI_AS, which is not a UTF-8 collation");
        _schema.Calls.ShouldBe(["report"]);
    }

    [Fact]
    public async Task Initialize_WhenAProvisionedDatabaseHasAnotherCollationThanTheConfiguredOne_ShouldRefuse()
    {
        // Arrange
        _database.Collation = new("Latin1_General_100_CI_AS_SC_UTF8", Utf8, null, null);

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain($"has collation Latin1_General_100_CI_AS_SC_UTF8 but Nightingale:Store:Collation is {NightingaleOptions.StoreOptions.DefaultCollation}");
        _schema.Calls.ShouldBe(["report"]);
    }

    [Theory]
    [InlineData(SchemaMode.Verify)]
    [InlineData(SchemaMode.Migrate)]
    [InlineData(SchemaMode.Initialize)]
    public async Task Initialize_WhenTheStoreIsNewerThanTheServer_ShouldRefuseWhateverTheMode(SchemaMode mode)
    {
        // Arrange
        _schema.Report = Initialized() with { UnknownMigrations = ["29990101000000_FromTheFuture"] };

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(mode));

        // Assert
        exception.Message.ShouldContain("newer than this server");
        exception.Message.ShouldContain("29990101000000_FromTheFuture");
        _schema.Calls.ShouldBe(["report"]);
    }

    [Theory]
    [InlineData(SchemaMode.Migrate)]
    [InlineData(SchemaMode.Initialize)]
    public async Task Initialize_WhenTheConfigurationContradictsTheStoresSettings_ShouldRefuseEvenWhenAllowedToChange(SchemaMode mode)
    {
        // Arrange
        _schema.Report = Initialized() with { SettingConflicts = ["Partitioning is Tenant but the store was initialized with None"] };

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(mode));

        // Assert
        exception.Message.ShouldContain("Partitioning is Tenant but the store was initialized with None");
        exception.Message.ShouldContain("fixed at initialization");
        _schema.Calls.ShouldBe(["report"]);
    }

    [Fact]
    public async Task Initialize_WhenAnInitializedStoreIsNotUtf8_ShouldRefuseUnlessTheHostIgnoresIt()
    {
        // Arrange
        _schema.Report = Initialized();
        _database.Collation = new("SQL_Latin1_General_CP1_CI_AS", Latin1, null, null);

        // Act
        var refused = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(SchemaMode.Verify));
        _options.Store.IgnoreCollationCompatibility = true;
        await InitializeAsync(SchemaMode.Verify);

        // Assert
        refused.Message.ShouldContain("is not a UTF-8 collation");
        _schema.Calls.ShouldBe(["report", "report", "read created by"]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Initialize_WhenTheSchemaIsBehind_ShouldRefuseToVerifyWithWhatWouldBeAppliedAndApplyOtherwise(bool storeChanges, bool pendingMigration)
    {
        // Arrange
        _schema.Report = Initialized() with
        {
            StoreChanges = storeChanges ? "CREATE INDEX ix_pc_events_category_seq" : null,
            PendingMigrations = pendingMigration ? ["20270101000000_Next"] : [],
        };

        // Act
        var refused = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(SchemaMode.Verify));
        await InitializeAsync(SchemaMode.Migrate);
        await InitializeAsync(SchemaMode.Initialize);

        // Assert: applying is the tables, then the record of what made them; never the
        // initialization record, which an initialized store has.
        refused.Message.ShouldContain("Nightingale:SchemaMode is Verify");
        refused.Message.ShouldContain(storeChanges ? "ix_pc_events_category_seq" : "20270101000000_Next");
        refused.Message.ShouldContain("--apply-schema");
        _schema.Calls.ShouldBe(["report", "report", "apply", "record store", "read created by", "report", "apply", "record store", "read created by"]);
        _schema.Initialization.ShouldBeNull();
    }

    [Theory]
    [InlineData(SchemaMode.Verify, "lock shared")]
    [InlineData(SchemaMode.Migrate, "lock exclusive")]
    [InlineData(SchemaMode.Initialize, "lock exclusive")]
    public async Task Initialize_WhenTheStoreIsCurrent_ShouldServeItChangingNothingUnderTheLockTheModeTakes(SchemaMode mode, string lockTaken)
    {
        // Arrange
        _schema.Report = Initialized();

        // Act
        await InitializeAsync(mode);

        // Assert
        _database.Calls.ShouldBe(["exists", lockTaken, "collation", "unlock"]);
        _schema.Calls.ShouldBe(["report", "read created by"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_ShouldPassFastBootOnToTheReport(bool fastBoot)
    {
        // Arrange
        _schema.Report = Initialized();

        // Act
        await InitializeAsync(fastBoot: fastBoot);

        // Assert
        _schema.TrustedStoreHash.ShouldBe(fastBoot);
    }

    [Fact]
    public async Task Initialize_WhenTheStoreIsCurrentUnderARecordThatIsNotThisServers_ShouldRewriteTheRecordUnlessVerifying()
    {
        // Arrange: the tables are what this server expects, the recorded library or hash is not.
        _schema.Report = Initialized() with { StoreRecordStale = true };

        // Act
        await InitializeAsync(SchemaMode.Verify);
        await InitializeAsync(SchemaMode.Migrate);

        // Assert
        _schema.Calls.ShouldBe(["report", "read created by", "report", "record store", "read created by"]);
    }

    [Fact]
    public async Task Start_ShouldInitializeWithWhatTheOptionsSay()
    {
        // Arrange
        _schema.Report = Initialized() with { PendingMigrations = ["20270101000000_Next"] };
        _options.SchemaMode = SchemaMode.Migrate;
        _options.FastBoot = true;

        // Act
        await Initializer().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        _schema.Calls.ShouldContain("apply");
        _schema.TrustedStoreHash.ShouldBe(true);
    }

    [Fact]
    public async Task Start_ByDefault_ShouldOnlyVerify()
    {
        // Arrange: a store that is behind, under the options as a host gets them.
        _schema.Report = Initialized() with { PendingMigrations = ["20270101000000_Next"] };

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => Initializer().StartAsync(TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("Nightingale:SchemaMode is Verify");
        _database.Calls.ShouldContain("lock shared");
        _schema.Calls.ShouldNotContain("apply");
    }

    private static SchemaReport Initialized() => new(true, true, null, [], [], []);

    private StoreInitializer Initializer() =>
        new(_database, _schema, _options, new NameComparison(), new FakeTimeProvider(Now), NullLogger<StoreInitializer>.Instance);

    private Task InitializeAsync(SchemaMode mode = SchemaMode.Initialize, bool fastBoot = false) =>
        Initializer().InitializeAsync(mode, fastBoot, TestContext.Current.CancellationToken);

    /// <summary>A database that answers what the test set and records what it was asked, the lock's taking and release included. It exists, is empty and has the default collation unless said otherwise.</summary>
    private sealed class FakeDatabase : IStoreDatabase
    {
        public string Name { get; set; } = "nightingale";

        public bool Exists { get; set; } = true;

        public List<DatabaseTable> Tables { get; } = [];

        public DatabaseCollation Collation { get; set; } = new(NightingaleOptions.StoreOptions.DefaultCollation, Utf8, 0, 1033);

        public Dictionary<string, int?> CodePages { get; } = new(StringComparer.Ordinal) { [NightingaleOptions.StoreOptions.DefaultCollation] = Utf8 };

        public List<string> Calls { get; } = [];

        public Task<bool> ExistsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("exists");
            return Task.FromResult(Exists);
        }

        public Task<int?> ReadCodePageAsync(string collation, CancellationToken cancellationToken)
        {
            Calls.Add($"code page of {collation}");
            return Task.FromResult(CodePages[collation]);
        }

        public Task CreateAsync(string? collation, CancellationToken cancellationToken)
        {
            Calls.Add($"create {collation ?? "(server default)"}");
            Exists = true;
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> LockSchemaAsync(bool exclusive, CancellationToken cancellationToken)
        {
            Calls.Add(exclusive ? "lock exclusive" : "lock shared");
            return Task.FromResult<IAsyncDisposable>(new Held(Calls));
        }

        public Task<DatabaseCollation> ReadCollationAsync(CancellationToken cancellationToken)
        {
            Calls.Add("collation");
            return Task.FromResult(Collation);
        }

        public Task<IReadOnlyList<DatabaseTable>> ListTablesAsync(CancellationToken cancellationToken)
        {
            Calls.Add("list tables");
            return Task.FromResult<IReadOnlyList<DatabaseTable>>(Tables);
        }

        private sealed class Held(List<string> calls) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                calls.Add("unlock");
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A schema that reports what the test set and records what it was asked. It reports a database that is not initialized unless said otherwise, and owns the store's two tables and the migrations' history.</summary>
    private sealed class FakeSchema : IStoreSchema
    {
        public SchemaReport Report { get; set; } = new(true, false, null, ["20260101000000_Initial"], [], []);

        public HashSet<TableName> OwnTables { get; } = [new("dbo", "pc_events"), new("dbo", "pc_streams"), new("nightingale", "__EFMigrationsHistory")];

        public List<string> Calls { get; } = [];

        public bool? TrustedStoreHash { get; private set; }

        public (IStoreSettings Settings, DateTimeOffset CreatedAt, string CreatedBy)? Initialization { get; private set; }

        public Task<SchemaReport> ReportAsync(bool trustStoreHash, CancellationToken cancellationToken)
        {
            Calls.Add("report");
            TrustedStoreHash = trustStoreHash;
            return Task.FromResult(Report);
        }

        public Task<IReadOnlySet<TableName>> ListOwnTablesAsync(CancellationToken cancellationToken)
        {
            Calls.Add("own tables");
            return Task.FromResult<IReadOnlySet<TableName>>(OwnTables);
        }

        public Task ApplyAsync(CancellationToken cancellationToken)
        {
            Calls.Add("apply");
            return Task.CompletedTask;
        }

        public Task RecordStoreAsync(CancellationToken cancellationToken)
        {
            Calls.Add("record store");
            return Task.CompletedTask;
        }

        public Task WriteInitializationAsync(IStoreSettings settings, DateTimeOffset createdAt, string createdBy, CancellationToken cancellationToken)
        {
            Calls.Add("write initialization");
            Initialization = (settings, createdAt, createdBy);
            return Task.CompletedTask;
        }

        public Task<string> ReadCreatedByAsync(CancellationToken cancellationToken)
        {
            Calls.Add("read created by");
            return Task.FromResult("a server");
        }
    }
}
