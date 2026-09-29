using Microsoft.Extensions.Configuration;
using Npgsql;
using Sa.Configuration.PostgreSql;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Configuration.PostgreSqlTests;


/// <summary>
/// Covers the load pipeline: the retry loop, data-source lifetime, the async entry point,
/// argument validation and layering. Row-mapping details are covered by
/// <see cref="DatabaseConfigurationProviderTests"/>.
/// </summary>
[Trait("Category", "Local")]
public sealed class DatabaseConfigurationLoadTests(DatabaseConfigurationLoadTests.Fixture fixture)
    : IClassFixture<DatabaseConfigurationLoadTests.Fixture>
{
    public sealed class Fixture : PgDataSourceFixture
    {
        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();

            using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            using (var createTableCommand = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS config_load_test (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );", connection))
            {
                await createTableCommand.ExecuteNonQueryAsync();
            }

            using var insertCommand = new NpgsqlCommand(@"
                INSERT INTO config_load_test (key, value) VALUES
                    ('NormalKey', 'normal_value'),
                    ('EmptyValueKey', ''),
                    ('NullValueKey', null)
                ON CONFLICT (key) DO NOTHING;", connection);

            await insertCommand.ExecuteNonQueryAsync();

            // No primary key: lets a test prove which of two duplicate rows wins.
            using (var createDupesCommand = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS config_load_dupes (
                    key TEXT,
                    value TEXT,
                    ord INT
                );", connection))
            {
                await createDupesCommand.ExecuteNonQueryAsync();
            }

            using var insertDupesCommand = new NpgsqlCommand(@"
                INSERT INTO config_load_dupes (key, value, ord) VALUES
                    ('dup', 'first', 1),
                    ('dup', 'last', 2)
                ON CONFLICT DO NOTHING;", connection);

            await insertDupesCommand.ExecuteNonQueryAsync();
        }
    }

    private const string SelectSql = "SELECT key, value FROM config_load_test";
    private const string SingleRowSql = "SELECT key, value FROM config_load_test WHERE key = 'NormalKey'";
    private const string DupesSql = "SELECT key, value FROM config_load_dupes ORDER BY ord";


    #region Retry

    /// <summary>
    /// Guards the premise of the retry tests. If Npgsql ever stops classifying SQLSTATE 08006
    /// as transient they would pass for the wrong reason, so the dependency is asserted
    /// explicitly and fails with a clear message instead.
    /// </summary>
    [Fact]
    public void ConnectionFailure_IsClassifiedAsTransient_ByNpgsql()
    {
        Assert.True(ConnectionFailure().IsTransient);
        Assert.False(UndefinedTableFailure().IsTransient);
    }

    [Fact]
    public void Load_Retries_AndSucceeds_AfterTransientFailures()
    {
        // Arrange
        using var dataSource = DataSource(failuresBeforeSuccess: 2);

        var builder = new ConfigurationBuilder();
        builder.Add(new DatabaseConfigurationSource(Options(SingleRowSql), dataSource));

        // Act
        var configuration = builder.Build();

        // Assert — the load survived two transient failures and the data is intact.
        Assert.Equal(3, dataSource.Attempts);
        Assert.Equal("normal_value", configuration["NormalKey"]);
    }

    [Fact]
    public void Load_DoesNotRetry_AfterNonTransientFailure()
    {
        // Arrange
        using var dataSource = DataSource(failuresBeforeSuccess: int.MaxValue, failure: FailureKind.NonTransient);

        var builder = new ConfigurationBuilder();
        builder.Add(new DatabaseConfigurationSource(Options(SingleRowSql), dataSource));

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());

        // Assert — a permanent error fails fast, keeping the original exception.
        Assert.Equal(1, dataSource.Attempts);
        Assert.IsType<PostgresException>(ex.InnerException);
    }

    [Fact]
    public void Load_GivesUp_AfterMaxAttempts_AndSurfacesInnerException()
    {
        // Arrange — MaxAttempts counts total attempts, so 2 means one try plus one retry.
        using var dataSource = DataSource(failuresBeforeSuccess: int.MaxValue);

        var builder = new ConfigurationBuilder();
        builder.Add(new DatabaseConfigurationSource(Options(SingleRowSql, maxAttempts: 2), dataSource));

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());

        // Assert — attempts stop at the ceiling, then the original exception is preserved.
        Assert.Equal(2, dataSource.Attempts);
        Assert.IsType<PostgresException>(ex.InnerException);
    }

    [Fact]
    public void Load_HonoursMaxAttemptsOne_AndFailsOnFirstAttempt()
    {
        // Arrange
        using var dataSource = DataSource(failuresBeforeSuccess: int.MaxValue);

        var builder = new ConfigurationBuilder();
        builder.Add(new DatabaseConfigurationSource(Options(SingleRowSql, maxAttempts: 1), dataSource));

        // Act
        Assert.Throws<InvalidOperationException>(() => builder.Build());

        // Assert
        Assert.Equal(1, dataSource.Attempts);
    }

    [Fact]
    public void Load_KeepsPreviousData_WhenReloadFails()
    {
        // Arrange: a healthy first load
        using var dataSource = DataSource(failuresBeforeSuccess: 0);

        var builder = new ConfigurationBuilder();
        builder.Add(new DatabaseConfigurationSource(Options(SingleRowSql, maxAttempts: 1), dataSource));
        var configuration = builder.Build();
        Assert.Equal("normal_value", configuration["NormalKey"]);

        // Act: the next load fails permanently
        dataSource.FailEverything(FailureKind.NonTransient);
        Assert.Throws<InvalidOperationException>(() => configuration.Reload());

        // Assert — a failed reload must not wipe the working configuration.
        Assert.Equal("normal_value", configuration["NormalKey"]);
    }

    #endregion


    #region Data source lifetime

    [Fact]
    public void Reload_ReusesTheSameDataSource_InsteadOfRecreatingThePool()
    {
        // Arrange
        using var dataSource = DataSource(failuresBeforeSuccess: 0);

        var builder = new ConfigurationBuilder();
        builder.Add(new DatabaseConfigurationSource(Options(SelectSql), dataSource));
        var configuration = builder.Build();

        // Act
        configuration.Reload();
        configuration.Reload();

        // Assert — one query per load, all three through the same data source instance,
        // which was never disposed in between.
        Assert.Equal(3, dataSource.Attempts);
        Assert.Equal(0, dataSource.DisposeCount);
    }

    [Fact]
    public void Dispose_ReleasesOwnedDataSource_ExactlyOnce()
    {
        // Arrange
        var dataSource = DataSource(failuresBeforeSuccess: 0);

        var provider = new DatabaseConfigurationProvider(
            Options(SelectSql), dataSource, ownsDataSource: true);
        provider.Load();

        // Act
        provider.Dispose();
        provider.Dispose();

        // Assert
        Assert.Equal(1, dataSource.DisposeCount);
    }

    [Fact]
    public void Dispose_LeavesBorrowedDataSource_Alone()
    {
        // Arrange
        var dataSource = DataSource(failuresBeforeSuccess: 0);

        var provider = new DatabaseConfigurationProvider(Options(SelectSql), dataSource);
        provider.Load();

        // Act
        provider.Dispose();

        // Assert — the application owns the data source, so the provider must not close it.
        Assert.Equal(0, dataSource.DisposeCount);
    }

    [Fact]
    public void ConfigurationRoot_Disposes_OwnedDataSource()
    {
        // Arrange — a source that builds an *owning* provider, so the pool has to be released
        // by whoever disposes the configuration root.
        var dataSource = DataSource(failuresBeforeSuccess: 0);
        var builder = new ConfigurationBuilder();
        builder.Add(new FixedProviderSource(
            new DatabaseConfigurationProvider(Options(SelectSql), dataSource, ownsDataSource: true)));

        // Act — IConfigurationRoot itself is not IDisposable; the concrete ConfigurationRoot is.
        var root = builder.Build();
        Assert.Equal(0, dataSource.DisposeCount);
        ((IDisposable)root).Dispose();

        // Assert — ConfigurationRoot must reach the provider's IDisposable.
        Assert.Equal(1, dataSource.DisposeCount);
    }

    #endregion


    #region Async entry point

    [Fact]
    public async Task LoadAsync_LoadsData_AndRaisesReloadToken()
    {
        // Arrange
        using var dataSource = DataSource(failuresBeforeSuccess: 0);
        var provider = new DatabaseConfigurationProvider(Options(SelectSql), dataSource);

        var fired = 0;
        using var registration = provider.GetReloadToken()
            .RegisterChangeCallback(_ => Interlocked.Increment(ref fired), state: null);

        // Act
        await provider.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, dataSource.Attempts);
        Assert.Equal(1, fired);
        Assert.True(provider.TryGet("NormalKey", out var value));
        Assert.Equal("normal_value", value);
    }

    [Fact]
    public async Task LoadAsync_SurfacesCancellation_WithoutWrapping()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var dataSource = DataSource(failuresBeforeSuccess: 0);
        var provider = new DatabaseConfigurationProvider(Options(SelectSql), dataSource);

        // Act — cancellation must stay observable as cancellation, not as a wrapped load failure.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.LoadAsync(cts.Token).AsTask());

        // Assert
        Assert.IsNotType<InvalidOperationException>(ex);
        Assert.Equal(0, dataSource.Attempts);
    }

    #endregion


    #region Validation

    [Fact]
    public void AddSaPostgreSqlConfiguration_Throws_OnNullBuilder()
    {
        IConfigurationBuilder builder = null!;
        Assert.Throws<ArgumentNullException>(() => builder.AddSaPostgreSqlConfiguration(Options(SelectSql)));
    }

    [Fact]
    public void AddSaPostgreSqlConfiguration_Throws_OnNullOptions()
    {
        var builder = new ConfigurationBuilder();
        Assert.Throws<ArgumentNullException>(
            () => builder.AddSaPostgreSqlConfiguration((PostgreSqlConfigurationOptions)null!));
    }

    [Fact]
    public void AddSaPostgreSqlConfiguration_Throws_OnBlankConnectionString()
    {
        var builder = new ConfigurationBuilder();
        Assert.Throws<ArgumentException>(
            () => builder.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions("  ", SelectSql)));
    }

    [Fact]
    public void AddSaPostgreSqlConfiguration_Throws_OnBlankSelectSql()
    {
        var builder = new ConfigurationBuilder();
        Assert.Throws<ArgumentException>(
            () => builder.AddSaPostgreSqlConfiguration(
                new PostgreSqlConfigurationOptions(fixture.ConnectionString, " ")));
    }

    [Fact]
    public void AddSaPostgreSqlConfiguration_Throws_OnNegativeMaxAttempts()
    {
        var builder = new ConfigurationBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddSaPostgreSqlConfiguration(
                new PostgreSqlConfigurationOptions(fixture.ConnectionString, SelectSql) { MaxAttempts = -1 }));
    }

    [Fact]
    public void AddSaPostgreSqlConfiguration_Throws_OnNullSharedDataSource()
    {
        var builder = new ConfigurationBuilder();
        Assert.Throws<ArgumentNullException>(
            () => builder.AddSaPostgreSqlConfiguration(
                new PostgreSqlConfigurationOptions(fixture.ConnectionString, SelectSql), null!));
    }

    [Fact]
    public void AddSaPostgreSqlConfiguration_AllowsBlankConnectionString_WithSharedDataSource()
    {
        // The data source carries the connection, so the connection string is irrelevant here.
        using var dataSource = DataSource(failuresBeforeSuccess: 0);

        var builder = new ConfigurationBuilder();
        builder.AddSaPostgreSqlConfiguration(
            new PostgreSqlConfigurationOptions(string.Empty, SingleRowSql), dataSource);

        Assert.Equal("normal_value", builder.Build()["NormalKey"]);
    }

    [Fact]
    public void ToString_RedactsSecrets()
    {
        // Neither parameter values nor the connection string's password may reach a log line
        // through an interpolated options object.
        var options = new PostgreSqlConfigurationOptions(
            "Host=db;Database=app;Password=conn-secret",
            SelectSql,
            new NpgsqlParameter("pwd", "param-secret"));

        var text = options.ToString();

        Assert.DoesNotContain("param-secret", text);
        Assert.DoesNotContain("conn-secret", text);
        Assert.Contains("***", text);
        Assert.Contains("Host=db", text);
        Assert.Contains(nameof(PostgreSqlConfigurationOptions), text);
    }

    #endregion


    #region Layering semantics

    [Fact]
    public void LastWins_IsTheDefault_ForDuplicateKeys()
    {
        // Arrange
        var builder = new ConfigurationBuilder();
        builder.Add(new DatabaseConfigurationSource(Options(DupesSql), DataSource(failuresBeforeSuccess: 0)));

        // Act
        var configuration = builder.Build();

        // Assert
        Assert.Equal("last", configuration["dup"]);
    }

    [Fact]
    public void FirstWins_CanBeSelected_ForDuplicateKeys()
    {
        // Arrange
        var builder = new ConfigurationBuilder();
        builder.Add(new DatabaseConfigurationSource(
            Options(DupesSql) with { LastWins = false }, DataSource(failuresBeforeSuccess: 0)));

        // Act
        var configuration = builder.Build();

        // Assert
        Assert.Equal("first", configuration["dup"]);
    }

    [Fact]
    public void SkipEmptyValues_DelegatesBlankValue_ToLowerPrioritySource()
    {
        // Arrange — the DB has an empty string for EmptyValueKey; with the default it is
        // dropped, so the in-memory source lower down answers for that key instead.
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["EmptyValueKey"] = "from-lower" });
        builder.Add(new DatabaseConfigurationSource(Options(SelectSql), DataSource(failuresBeforeSuccess: 0)));

        // Act
        var configuration = builder.Build();

        // Assert
        Assert.Equal("from-lower", configuration["EmptyValueKey"]);
    }

    [Fact]
    public void SkipEmptyValuesFalse_OverridesLowerPrioritySource_WithEmptyString()
    {
        // Arrange
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["EmptyValueKey"] = "from-lower" });
        builder.Add(new DatabaseConfigurationSource(
            Options(SelectSql) with { SkipEmptyValues = false }, DataSource(failuresBeforeSuccess: 0)));

        // Act
        var configuration = builder.Build();

        // Assert — the key now exists in the database source, so it wins over the lower one.
        Assert.Equal(string.Empty, configuration["EmptyValueKey"]);
    }

    #endregion


    private FaultInjectingDataSource DataSource(int failuresBeforeSuccess, FailureKind failure = FailureKind.Transient)
        => new(IPgDataSource.Create(fixture.ConnectionString), failuresBeforeSuccess, failure);

    private PostgreSqlConfigurationOptions Options(string selectSql, int maxAttempts = 3)
        => new(fixture.ConnectionString, selectSql)
        {
            // Keep the suite fast: the first retry is always immediate.
            MaxAttempts = maxAttempts,
            MedianFirstRetryDelay = 1,
        };

    private static PostgresException ConnectionFailure()
        => new("simulated connection failure", "ERROR", "ERROR", "08006");

    private static PostgresException UndefinedTableFailure()
        => new("relation does not exist", "ERROR", "ERROR", "42P01");

    private enum FailureKind { Transient, NonTransient }

    /// <summary>Wraps a real data source, so the succeeding attempt runs against a real database.</summary>
    private sealed class FaultInjectingDataSource : IPgDataSource
    {
        private readonly IPgDataSource inner;
        private readonly int failuresBeforeSuccess;
        private FailureKind failure;
        private bool forceFailure;

        public FaultInjectingDataSource(IPgDataSource inner, int failuresBeforeSuccess, FailureKind failure)
        {
            this.inner = inner;
            this.failuresBeforeSuccess = failuresBeforeSuccess;
            this.failure = failure;
        }

        public int Attempts { get; private set; }
        public int DisposeCount { get; private set; }

        /// <summary>Switches the double into a state where every subsequent read fails.</summary>
        public void FailEverything(FailureKind failure)
        {
            this.failure = failure;
            forceFailure = true;
        }

        public async Task<int> ExecuteReader(
            string sql,
            Action<NpgsqlDataReader, int> read,
            Action<NpgsqlCommand>? initCommand,
            CancellationToken cancellationToken = default)
        {
            Attempts++;

            if (forceFailure || Attempts <= failuresBeforeSuccess)
                throw failure == FailureKind.Transient ? ConnectionFailure() : UndefinedTableFailure();

            return await inner.ExecuteReader(sql, read, initCommand, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => DisposeCount++;
        public ValueTask DisposeAsync() => default;

        public string GetSearchPath() => inner.GetSearchPath();

        public ValueTask<NpgsqlConnection> OpenDbConnection(CancellationToken cancellationToken)
            => inner.OpenDbConnection(cancellationToken);

        public Task<int> ExecuteNonQuery(
            string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
            => inner.ExecuteNonQuery(sql, initCommand, cancellationToken);

        public Task<object?> ExecuteScalar(
            string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
            => inner.ExecuteScalar(sql, initCommand, cancellationToken);

        public ValueTask<ulong> BeginBinaryImport(
            string sql,
            Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write,
            CancellationToken cancellationToken = default)
            => inner.BeginBinaryImport(sql, write, cancellationToken);
    }

    /// <summary>Hands a pre-built provider to the builder, so the test controls ownership.</summary>
    private sealed class FixedProviderSource(DatabaseConfigurationProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }
}
