using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Postgres;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.PostgresTests;

/// <summary>
/// Keyed registration of PostgreSQL file storages: several independent storages in one container,
/// each with its own settings, its own partitional module (its own migration and cleanup) and its
/// own table, plus the unkeyed alias that feeds the hybrid failover chain.
/// </summary>
/// <remarks>
/// No database is started: the scenarios build the registration and read the named options (which is
/// what runs validation). Storage/database behaviour lives in the Testcontainers-backed fixtures.
/// </remarks>
public sealed class PostgresFileStorageKeyedRegistrationTests
{
    private static IServiceCollection WithStorage(
        IServiceCollection services, string key, string tableName)
        => services.AddSaPostgreSqlFileStorage(key, b =>
            b.Options(ob => ob.Configure(o => o.TableName = tableName)));

    // ---------- several storages in one container ----------

    [Fact]
    public void Register_TwoKeys_ResolveIndependentOptions()
    {
        var services = WithStorage(new ServiceCollection(), "one", "files_one");
        WithStorage(services, "two", "files_two");

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<PostgresFileStorageOptions>>();

        Assert.Equal("files_one", monitor.Get("one").TableName);
        Assert.Equal("files_two", monitor.Get("two").TableName);
    }

    [Fact]
    public void Register_TwoKeys_EachGetsItsOwnKeyedStorageAndUnkeyedAlias()
    {
        var services = WithStorage(new ServiceCollection(), "one", "files_one");
        WithStorage(services, "two", "files_two");

        // One keyed IFileStorage per registration...
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(IFileStorage) && d.ServiceKey is not null));
        // ...and one unkeyed alias each, which is what the hybrid container collects.
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(IFileStorage) && d.ServiceKey is null));
    }

    [Fact]
    public void Register_TwoKeys_HaveIndependentPartitionalModules()
    {
        var services = WithStorage(new ServiceCollection(), "one", "files_one");
        WithStorage(services, "two", "files_two");

        string one = PostgresFileStorageRegistrar.ModuleKey("one");
        string two = PostgresFileStorageRegistrar.ModuleKey("two");

        Assert.NotEqual(one, two);
        Assert.Contains(services, d => d.ServiceType == typeof(Sa.Partitional.PostgreSql.IPartitionManager) && Equals(d.ServiceKey, one));
        Assert.Contains(services, d => d.ServiceType == typeof(Sa.Partitional.PostgreSql.IPartitionManager) && Equals(d.ServiceKey, two));
    }

    [Fact]
    public void Register_Marker_CarriesKeyModuleKeyAndOptionsName()
    {
        var services = WithStorage(new ServiceCollection(), "share", "files_share");

        using var provider = services.BuildServiceProvider();
        var marker = provider.GetRequiredKeyedService<PostgresFileStorageRegistration>("share");

        Assert.Equal("share", marker.Key);
        Assert.Equal(PostgresFileStorageRegistrar.ModuleKey("share"), marker.ModuleKey);
        Assert.Equal("share", marker.OptionsName);
    }

    // ---------- required and unique table name ----------

    [Fact]
    public void Register_MissingTableName_FailsAtFirstRead()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage("one");

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<PostgresFileStorageOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() => monitor.Get("one"));
        Assert.Contains(nameof(PostgresFileStorageOptions.TableName), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_TwoKeysWithTheSameTableName_FailAtFirstRead()
    {
        // Two storages over one physical table would run two migration and cleanup schedules over
        // it, each free to drop what the other still needs — rejected instead.
        var services = WithStorage(new ServiceCollection(), "one", "shared_table");
        WithStorage(services, "two", "shared_table");

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<PostgresFileStorageOptions>>();

        // The first one claims the table...
        Assert.Equal("shared_table", monitor.Get("one").TableName);

        // ...the second is refused, naming the owner.
        var ex = Assert.Throws<OptionsValidationException>(() => monitor.Get("two"));
        Assert.Contains("shared_table", ex.Message, StringComparison.Ordinal);
        Assert.Contains("one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_SameKeyTwice_ThrowsAtRegistration()
    {
        var services = WithStorage(new ServiceCollection(), "one", "files_one");

        var ex = Assert.Throws<InvalidOperationException>(
            () => WithStorage(services, "one", "files_other"));

        Assert.Contains("already registered", ex.Message, StringComparison.Ordinal);
    }

    // ---------- embedding into the hybrid failover pipeline ----------

    [Fact]
    public void Register_Storages_FeedTheHybridChain()
    {
        var services = new ServiceCollection();
        Sa.HybridFileStorage.Setup.AddSaHybridFileStorage(services);

        WithStorage(services, "one", "files_one");
        WithStorage(services, "two", "files_two");

        // The hybrid entry point is present and both keyed storages are visible to it (unkeyed)
        // in registration order — the failover chain is built from these.
        Assert.Contains(services, d => d.ServiceType == typeof(Sa.HybridFileStorage.IHybridFileStorage));

        var unkeyed = services
            .Where(d => d.ServiceType == typeof(IFileStorage) && d.ServiceKey is null)
            .ToArray();

        Assert.Equal(2, unkeyed.Length);
    }

    // ---------- keyed fluent channel (AddDataSource per registration) ----------

    [Fact]
    public void Register_Chained_ReturnsThisRegistrationsPartitionConfiguration()
    {
        var services = new ServiceCollection();
        var config = services.AddSaPostgreSqlFileStorageChained("one", b =>
            b.Options(ob => ob.Configure(o => o.TableName = "files_one")));

        Assert.NotNull(config);
        Assert.Contains(services, d => d.ServiceType == typeof(PostgresFileStorageRegistration) && Equals(d.ServiceKey, "one"));
    }

    // ---------- configuration section binding ----------

    [Fact]
    public void Register_BindsPartByRangeFromConfiguration()
    {
        // Regression: PgPartBy cannot be bound from configuration — the type carries delegate-based
        // formatting/parsing and has no public instance constructor, so the source-generated binder
        // would emit SYSLIB1100/SYSLIB1101 and silently skip the property. The options expose the
        // real PartByRange enum instead, and it must be read from the section.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:TableName"] = "files",
                ["Storage:PartByRange"] = "Month",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaPostgreSqlFileStorage("files", b => b.FromConfiguration("Storage"));

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<PostgresFileStorageOptions>>();

        PostgresFileStorageOptions options = monitor.Get("files");
        Assert.Equal(Sa.Partitional.PostgreSql.PartByRange.Month, options.PartByRange);
        Assert.Equal(
            Sa.Partitional.PostgreSql.PgPartBy.Month,
            Sa.Partitional.PostgreSql.PgPartBy.FromRange(options.PartByRange));
    }

    // ---------- automatic connection to an existing NpgsqlDataSource ----------

    [Fact]
    public void Register_WithoutAnIPgDataSource_ConnectsToTheRegisteredNpgsqlDataSource()
    {
        // The app has an NpgsqlDataSource (e.g. AddNpgsqlDataSource(...)) but no AddDataSource on
        // the chain and no default IPgDataSource: the storage must reuse that source, not fail.
        // Creating the source opens no connection, so this stays a no-database test.
        var borrowed = Npgsql.NpgsqlDataSource.Create(
            "Host=127.0.0.1;Database=postgres;Username=u;Password=p;Search Path=autoconn");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
                typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
            services.AddSingleton<Npgsql.NpgsqlDataSource>(borrowed);

            services.AddSaPostgreSqlFileStorage("files", b =>
                b.Options(ob => ob.Configure(o => o.TableName = "files")));

            using var provider = services.BuildServiceProvider();

            // The schema is auto-detected from the borrowed source's search path — proof the
            // registration resolved through the NpgsqlDataSource, not "public".
            Assert.Equal("autoconn",
                provider.GetRequiredKeyedService<PostgresFileStorageSchema>("files").Value);

            // And the storage itself resolves through the same source.
            Assert.NotNull(provider.GetRequiredKeyedService<IFileStorage>("files"));
        }
        finally
        {
            // Borrowed source: the storage never disposes it, so this test does.
            borrowed.Dispose();
        }
    }

    [Fact]
    public void Register_WithoutAnyDataSource_ThrowsAtFirstResolve()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddSaPostgreSqlFileStorage("files", b =>
            b.Options(ob => ob.Configure(o => o.TableName = "files")));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredKeyedService<IFileStorage>("files"));

        Assert.Contains("No IPgDataSource is registered", ex.Message, StringComparison.Ordinal);
    }

    // ---------- schedule settings ----------

    [Fact]
    public void Register_RetentionAndForwardDays_ReachTheSchedules()
    {
        // The two numbers are configured on the storage options but consumed by the partitional
        // module's schedules; a wiring mistake would silently keep the library defaults.
        var services = new ServiceCollection();
        services.AddSaPostgreSqlFileStorage("files", b => b.Options(ob => ob.Configure(o =>
        {
            o.TableName = "files";
            o.ExpireDays = 7;
            o.MigrationScheduleForwardDays = 4;
        })));

        using var provider = services.BuildServiceProvider();
        string moduleKey = PostgresFileStorageRegistrar.ModuleKey("files");

        MigrationScheduleSettings migration =
            provider.GetRequiredKeyedService<MigrationScheduleSettings>(moduleKey);
        PartCleanupScheduleSettings cleanup =
            provider.GetRequiredKeyedService<PartCleanupScheduleSettings>(moduleKey);

        Assert.True(migration.AsBackgroundJob);
        Assert.Equal(4, migration.ForwardDays);

        Assert.True(cleanup.AsBackgroundJob);
        Assert.Equal(TimeSpan.FromDays(7), cleanup.DropPartsAfterRetention);
    }

    // ---------- schema auto-detection from the search path ----------
    [Theory]
    [InlineData("Storage", "storage")]          // unquoted → PostgreSQL folds it to lower case
    [InlineData("storage,public", "storage")]   // only the first entry is effective
    [InlineData("'\"Storage\"'", "Storage")]    // a quoted entry is case-exact, quotes are not part of the name
    [InlineData("'\"my schema\"'", "public")]   // not a bare identifier → fall back to the default
    public void Schema_NormalisesTheAutoDetectedSearchPath(string searchPath, string expected)
    {
        // The value used to be taken verbatim, so `Search Path=Storage` silently targeted a
        // "Storage" schema instead of the folded `storage` PostgreSQL actually resolves, and a
        // quoted name produced SQL that does not parse.
        var borrowed = Npgsql.NpgsqlDataSource.Create(
            $"Host=127.0.0.1;Database=postgres;Username=u;Password=p;Search Path={searchPath}");

        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
                typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
            services.AddSingleton<Npgsql.NpgsqlDataSource>(borrowed);
            services.AddSaPostgreSqlFileStorage("files", b =>
                b.Options(ob => ob.Configure(o => o.TableName = "files")));

            using var provider = services.BuildServiceProvider();

            Assert.Equal(expected,
                provider.GetRequiredKeyedService<PostgresFileStorageSchema>("files").Value);
        }
        finally
        {
            borrowed.Dispose();
        }
    }
}
