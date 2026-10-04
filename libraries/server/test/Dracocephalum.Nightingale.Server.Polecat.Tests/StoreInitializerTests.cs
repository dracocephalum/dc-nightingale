using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// Every decision the initializer makes, against a database and a schema that only answer: when
/// it creates, when it initializes, when it applies and when it refuses, and what it says. What
/// the real database and the real schema do under those decisions is the integration tests'.
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

    [Fact]
    public async Task Initialize_WhenTheDatabaseIsMissingAndCreationIsOff_ShouldRefuseAndCreateNothing()
    {
        // Arrange
        _database.Exists = false;
        _options.CreateDatabase = false;

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain("Nightingale:CreateDatabase is false");
        _database.Calls.ShouldBe(["exists"]);
        _schema.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Initialize_WhenTheDatabaseIsMissing_ShouldCreateItWithTheConfiguredCollationThenInitializeIt()
    {
        // Arrange
        _database.Exists = false;
        _database.Collation = (NightingaleOptions.StoreOptions.DefaultCollation, Utf8);
        _options.Store.Partitioning = PartitioningMode.Tenant;
        _options.Store.AssignOrdinals = true;

        // Act
        await InitializeAsync();

        // Assert: the collation is checked before it is used, the store's tables come before the
        // record of what shaped them, and the record holds what is configured.
        _database.Calls.ShouldBe(["exists", $"code page of {NightingaleOptions.StoreOptions.DefaultCollation}", $"create {NightingaleOptions.StoreOptions.DefaultCollation}", "count tables", "collation"]);
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
        _database.Collation = ("Latin1_General_100_CI_AS_SC_UTF8", Utf8);
        _options.Store.Collation = null;

        // Act
        await InitializeAsync();

        // Assert
        _database.Calls.ShouldBe(["exists", "create (server default)", "count tables", "collation"]);
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
        _database.Collation = ("Latin1_General_100_BIN2", Latin1);
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
        _options.CreateDatabase = false;

        // Act
        await InitializeAsync();

        // Assert
        _database.Calls.ShouldBe(["exists", "count tables", "collation"]);
        _schema.Calls.ShouldBe(["report", "apply", "write initialization"]);
    }

    [Fact]
    public async Task Initialize_WhenTheDatabaseHoldsTablesAndNoneOfTheGatewaysMigrations_ShouldRefuse()
    {
        // Arrange
        _database.Tables = 3;

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain("holds 3 tables");
        exception.Message.ShouldContain("not initialized by Nightingale");
        _schema.Calls.ShouldBe(["report"]);
    }

    [Fact]
    public async Task Initialize_WhenAProvisionedDatabaseIsNotUtf8_ShouldRefuseAndInitializeNothing()
    {
        // Arrange
        _database.Collation = ("SQL_Latin1_General_CP1_CI_AS", Latin1);

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
        _database.Collation = ("Latin1_General_100_CI_AS_SC_UTF8", Utf8);

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());

        // Assert
        exception.Message.ShouldContain($"has collation Latin1_General_100_CI_AS_SC_UTF8 but Nightingale:Store:Collation is {NightingaleOptions.StoreOptions.DefaultCollation}");
        _schema.Calls.ShouldBe(["report"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_WhenTheStoreIsNewerThanTheServer_ShouldRefuseEvenWhenToldToApply(bool applyChanges)
    {
        // Arrange
        _schema.Report = Initialized() with { UnknownMigrations = ["29990101000000_FromTheFuture"] };

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(applyChanges));

        // Assert
        exception.Message.ShouldContain("newer than this server");
        exception.Message.ShouldContain("29990101000000_FromTheFuture");
        _schema.Calls.ShouldBe(["report"]);
    }

    [Fact]
    public async Task Initialize_WhenTheConfigurationContradictsTheStoresSettings_ShouldRefuseEvenWhenToldToApply()
    {
        // Arrange
        _schema.Report = Initialized() with { SettingConflicts = ["Partitioning is Tenant but the store was initialized with None"] };

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(applyChanges: true));

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
        _database.Collation = ("SQL_Latin1_General_CP1_CI_AS", Latin1);

        // Act
        var refused = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync());
        _options.Store.IgnoreCollationCompatibility = true;
        await InitializeAsync();

        // Assert
        refused.Message.ShouldContain("is not a UTF-8 collation");
        _schema.Calls.ShouldBe(["report", "report", "read created by"]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Initialize_WhenTheSchemaIsBehind_ShouldRefuseWithWhatWouldBeAppliedUnlessToldToApply(bool storeChanges, bool pendingMigration)
    {
        // Arrange
        _schema.Report = Initialized() with
        {
            StoreChanges = storeChanges ? "CREATE INDEX ix_pc_events_category_seq" : null,
            PendingMigrations = pendingMigration ? ["20270101000000_Next"] : [],
        };

        // Act
        var refused = await Should.ThrowAsync<StoreInitializationException>(() => InitializeAsync(applyChanges: false));
        await InitializeAsync(applyChanges: true);

        // Assert
        refused.Message.ShouldContain("Nightingale:ApplySchemaChanges is false");
        refused.Message.ShouldContain(storeChanges ? "ix_pc_events_category_seq" : "20270101000000_Next");
        refused.Message.ShouldContain("--apply-schema");
        _schema.Calls.ShouldBe(["report", "report", "apply", "read created by"]);
        _schema.Initialization.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_WhenTheStoreIsCurrent_ShouldServeItChangingNothingAndPassFastBootOn(bool fastBoot)
    {
        // Arrange
        _schema.Report = Initialized();

        // Act
        await InitializeAsync(fastBoot: fastBoot);

        // Assert
        _database.Calls.ShouldBe(["exists", "collation"]);
        _schema.Calls.ShouldBe(["report", "read created by"]);
        _schema.TrustedStoreHash.ShouldBe(fastBoot);
    }

    [Fact]
    public async Task Start_ShouldInitializeWithWhatTheOptionsSay()
    {
        // Arrange
        _schema.Report = Initialized() with { PendingMigrations = ["20270101000000_Next"] };
        _options.ApplySchemaChanges = true;
        _options.FastBoot = true;

        // Act
        await Initializer().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        _schema.Calls.ShouldContain("apply");
        _schema.TrustedStoreHash.ShouldBe(true);
    }

    private static SchemaReport Initialized() => new(true, true, null, [], [], []);

    private StoreInitializer Initializer() =>
        new(_database, _schema, _options, new FakeTimeProvider(Now), NullLogger<StoreInitializer>.Instance);

    private Task InitializeAsync(bool applyChanges = false, bool fastBoot = false) =>
        Initializer().InitializeAsync(applyChanges, fastBoot, TestContext.Current.CancellationToken);

    /// <summary>A database that answers what the test set and records what it was asked. It exists, is empty and has the default collation unless said otherwise.</summary>
    private sealed class FakeDatabase : IStoreDatabase
    {
        public string Name { get; set; } = "nightingale";

        public bool Exists { get; set; } = true;

        public int Tables { get; set; }

        public (string Collation, int? CodePage) Collation { get; set; } = (NightingaleOptions.StoreOptions.DefaultCollation, Utf8);

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

        public Task<(string Collation, int? CodePage)> ReadCollationAsync(CancellationToken cancellationToken)
        {
            Calls.Add("collation");
            return Task.FromResult(Collation);
        }

        public Task<int> CountTablesAsync(CancellationToken cancellationToken)
        {
            Calls.Add("count tables");
            return Task.FromResult(Tables);
        }
    }

    /// <summary>A schema that reports what the test set and records what it was asked. It reports a database that is not initialized unless said otherwise.</summary>
    private sealed class FakeSchema : IStoreSchema
    {
        public SchemaReport Report { get; set; } = new(true, false, null, ["20260101000000_Initial"], [], []);

        public List<string> Calls { get; } = [];

        public bool? TrustedStoreHash { get; private set; }

        public (IStoreSettings Settings, DateTimeOffset CreatedAt, string CreatedBy)? Initialization { get; private set; }

        public Task<SchemaReport> ReportAsync(bool trustStoreHash, CancellationToken cancellationToken)
        {
            Calls.Add("report");
            TrustedStoreHash = trustStoreHash;
            return Task.FromResult(Report);
        }

        public Task ApplyAsync(CancellationToken cancellationToken)
        {
            Calls.Add("apply");
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
