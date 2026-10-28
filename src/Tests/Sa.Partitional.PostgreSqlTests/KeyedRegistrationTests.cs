using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Data.PostgreSql;
using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSqlTests;

/// <summary>
/// Keyed registration of partitional modules: several independent modules in one container, each
/// with its own partition tables, migration/cleanup/cache settings and its own named schedule, plus
/// the "named data source when configured, otherwise the default one" data-source rule.
/// </summary>
/// <remarks>
/// No database is started: the data-source tests only build the <c>IPgDataSource</c> (creating one
/// opens no connection) and read its search path, which is what the module propagates into
/// <see cref="ISettingsBuilder.DefaultSchema"/>. PostgreSQL-backed scenarios live in the
/// integration tests that use Testcontainers.
/// </remarks>
public sealed class KeyedRegistrationTests
{
    const string ValidConnectionString = "Host=127.0.0.1;Database=postgres;Username=u;Password=p";

    static string WithSearchPath(string searchPath) => $"{ValidConnectionString};Search Path={searchPath}";

    static ServiceCollection CreateServices(bool withDefaultDataSource = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        if (withDefaultDataSource)
        {
            services.AddSaPostgreSqlDataSource(
                o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("public"))));
        }

        return services;
    }

    static IPartConfiguration AddKeyedModule(
        IServiceCollection services,
        string key,
        string tableName,
        bool? asBackgroundJob = null)
        => services.AddSaPartitional(key, (_, builder) => builder.AddSchema(schema => schema
            .AddTable(tableName, "id bigint NOT NULL", "created_at bigint NOT NULL")
            .PartByRange(PgPartBy.Day)), asBackgroundJob);

    // ---------- independence of modules ----------

    [Fact]
    public void Register_TwoKeys_ResolveIndependentModules()
    {
        ServiceCollection services = CreateServices();
        AddKeyedModule(services, "one", "t_one");
        AddKeyedModule(services, "two", "t_two");

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotSame(
            sp.GetRequiredKeyedService<IPartitionManager>("one"),
            sp.GetRequiredKeyedService<IPartitionManager>("two"));
        Assert.NotSame(
            sp.GetRequiredKeyedService<IPartRepository>("one"),
            sp.GetRequiredKeyedService<IPartRepository>("two"));
        Assert.NotSame(
            sp.GetRequiredKeyedService<IMigrationService>("one"),
            sp.GetRequiredKeyedService<IMigrationService>("two"));
        Assert.NotSame(
            sp.GetRequiredKeyedService<IPartCleanupService>("one"),
            sp.GetRequiredKeyedService<IPartCleanupService>("two"));

        ITableSettingsStorage one = sp.GetRequiredKeyedService<ISettingsBuilder>("one").Build();
        ITableSettingsStorage two = sp.GetRequiredKeyedService<ISettingsBuilder>("two").Build();

        Assert.Single(one.Tables);
        Assert.Single(two.Tables);
        Assert.Equal("public.t_one", one.Tables.Single().FullName);
        Assert.Equal("public.t_two", two.Tables.Single().FullName);
    }

    [Fact]
    public void Register_TwoKeys_HaveOwnSettings()
    {
        ServiceCollection services = CreateServices();

        AddKeyedModule(services, "one", "t_one")
            .AddPartMigrationSchedule(b => b.Configure((_, s) => s.ForwardDays = 7))
            .AddPartCleanupSchedule(b => b.Configure((_, s) => s.DropPartsAfterRetention = TimeSpan.FromDays(14)))
            .AddPartCache(b => b.Configure((_, s) => s.CacheTtl = TimeSpan.FromMinutes(1)));

        AddKeyedModule(services, "two", "t_two")
            .AddPartMigrationSchedule(b => b.Configure((_, s) => s.ForwardDays = 1))
            .AddPartCleanupSchedule(b => b.Configure((_, s) => s.DropPartsAfterRetention = TimeSpan.FromDays(60)))
            .AddPartCache(b => b.Configure((_, s) => s.CacheTtl = TimeSpan.FromMinutes(9)));

        using ServiceProvider sp = services.BuildServiceProvider();

        MigrationScheduleSettings migOne = sp.GetRequiredKeyedService<MigrationScheduleSettings>("one");
        MigrationScheduleSettings migTwo = sp.GetRequiredKeyedService<MigrationScheduleSettings>("two");
        Assert.NotSame(migOne, migTwo);
        Assert.Equal(7, migOne.ForwardDays);
        Assert.Equal(1, migTwo.ForwardDays);

        PartCleanupScheduleSettings cleanOne = sp.GetRequiredKeyedService<PartCleanupScheduleSettings>("one");
        PartCleanupScheduleSettings cleanTwo = sp.GetRequiredKeyedService<PartCleanupScheduleSettings>("two");
        Assert.NotSame(cleanOne, cleanTwo);
        Assert.Equal(TimeSpan.FromDays(14), cleanOne.DropPartsAfterRetention);
        Assert.Equal(TimeSpan.FromDays(60), cleanTwo.DropPartsAfterRetention);

        PartCacheSettings cacheOne = sp.GetRequiredKeyedService<PartCacheSettings>("one");
        PartCacheSettings cacheTwo = sp.GetRequiredKeyedService<PartCacheSettings>("two");
        Assert.NotSame(cacheOne, cacheTwo);
        Assert.Equal(TimeSpan.FromMinutes(1), cacheOne.CacheTtl);
        Assert.Equal(TimeSpan.FromMinutes(9), cacheTwo.CacheTtl);

        // the default (unkeyed) module was never registered
        Assert.Null(sp.GetService<MigrationScheduleSettings>());
        Assert.Null(sp.GetService<IPartitionManager>());
    }

    // ---------- a per-module named schedule with its own jobs ----------

    [Fact]
    public void Register_EachKeyedModule_GetsItsOwnNamedScheduleWithItsOwnJobs()
    {
        ServiceCollection services = CreateServices();
        AddKeyedModule(services, "one", "t_one", asBackgroundJob: true);
        AddKeyedModule(services, "two", "t_two", asBackgroundJob: true);

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotSame(
            sp.GetRequiredKeyedService<IScheduler>("one"),
            sp.GetRequiredKeyedService<IScheduler>("two"));

        IScheduleSettings one = sp.GetRequiredKeyedService<IScheduleSettings>("one");
        IScheduleSettings two = sp.GetRequiredKeyedService<IScheduleSettings>("two");

        Assert.True(one.IsHostedService);
        Assert.True(two.IsHostedService);

        Assert.Equal(
            ["Cleanup job [one]", "Migration job [one]"],
            one.GetJobSettings().Select(j => j.Properties.JobName).Order(StringComparer.Ordinal));

        Assert.Equal(
            ["Cleanup job [two]", "Migration job [two]"],
            two.GetJobSettings().Select(j => j.Properties.JobName).Order(StringComparer.Ordinal));
    }

    // ---------- repeated same-key registration merges settings but not jobs ----------

    [Fact]
    public void Register_SameKeyTwice_MergesTablesAndRegistersJobsOnce()
    {
        ServiceCollection services = CreateServices();
        AddKeyedModule(services, "one", "t_a", asBackgroundJob: true);
        AddKeyedModule(services, "one", "t_b", asBackgroundJob: true);

        using ServiceProvider sp = services.BuildServiceProvider();

        ITableSettingsStorage settings = sp.GetRequiredKeyedService<ISettingsBuilder>("one").Build();
        Assert.Equal(2, settings.Tables.Count);
        Assert.Contains(settings.Tables, t => t.FullName == "public.t_a");
        Assert.Contains(settings.Tables, t => t.FullName == "public.t_b");

        IJobSettings[] jobs = sp.GetRequiredKeyedService<IScheduleSettings>("one").GetJobSettings().ToArray();
        Assert.Equal(2, jobs.Length);
        Assert.Single(jobs, j => j.Properties.JobName == "Migration job [one]");
        Assert.Single(jobs, j => j.Properties.JobName == "Cleanup job [one]");
    }

    [Fact]
    public void Register_KeyedModule_DoesNotDisturbTheDefaultModule()
    {
        ServiceCollection services = CreateServices();

        services.AddSaPartitional((_, builder) => builder.AddSchema(schema => schema
            .AddTable("t_default", "id bigint NOT NULL", "created_at bigint NOT NULL")
            .PartByRange(PgPartBy.Day)));

        AddKeyedModule(services, "one", "t_one");

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Single(sp.GetRequiredService<ISettingsBuilder>().Build().Tables);
        Assert.Single(sp.GetRequiredKeyedService<ISettingsBuilder>("one").Build().Tables);

        Assert.NotNull(sp.GetRequiredService<IPartitionManager>());
        Assert.NotNull(sp.GetRequiredKeyedService<IPartitionManager>("one"));
    }

    // ---------- data source: named when configured, otherwise the default ----------

    [Fact]
    public void Register_KeyedModule_WithoutNamedDataSource_UsesTheDefaultDataSource()
    {
        ServiceCollection services = CreateServices(withDefaultDataSource: false);
        services.AddSaPostgreSqlDataSource(
            o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("def_schema"))));

        AddKeyedModule(services, "one", "t_one");

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Same(sp.GetRequiredService<IPgDataSource>(), PgDataSourceResolver.Resolve(sp, "one"));

        // the module's builder derives its default schema from the default data source
        Assert.Equal("def_schema", sp.GetRequiredKeyedService<ISettingsBuilder>("one").DefaultSchema);
        Assert.Equal("def_schema.t_one", sp.GetRequiredKeyedService<ISettingsBuilder>("one").Build().Tables.Single().FullName);

        // the repository resolves through the default data source (no named one exists)
        Assert.NotNull(sp.GetRequiredKeyedService<IPartRepository>("one"));
    }

    [Fact]
    public void Register_KeyedModule_WithNamedDataSource_UsesTheNamedOne()
    {
        ServiceCollection services = CreateServices(withDefaultDataSource: false);
        services.AddSaPostgreSqlDataSource(
            o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("def_schema"))));

        AddKeyedModule(services, "one", "t_one")
            .AddDataSource(ds => ds.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("mod_one"))));

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Same(
            sp.GetRequiredKeyedService<IPgDataSource>("one"),
            PgDataSourceResolver.Resolve(sp, "one"));
        Assert.Equal("mod_one", sp.GetRequiredKeyedService<IPgDataSource>("one").GetSearchPath());

        Assert.Equal("mod_one", sp.GetRequiredKeyedService<ISettingsBuilder>("one").DefaultSchema);
        Assert.Equal("mod_one.t_one", sp.GetRequiredKeyedService<ISettingsBuilder>("one").Build().Tables.Single().FullName);
    }

    [Fact]
    public void Register_TwoKeyedModules_ShareTheDefaultDataSourceWhenNeitherConfiguresOne()
    {
        ServiceCollection services = CreateServices(withDefaultDataSource: false);
        services.AddSaPostgreSqlDataSource(
            o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("shared"))));

        AddKeyedModule(services, "one", "t_one");
        AddKeyedModule(services, "two", "t_two");

        using ServiceProvider sp = services.BuildServiceProvider();

        IPgDataSource shared = sp.GetRequiredService<IPgDataSource>();
        Assert.Same(shared, PgDataSourceResolver.Resolve(sp, "one"));
        Assert.Same(shared, PgDataSourceResolver.Resolve(sp, "two"));

        Assert.Equal("shared", sp.GetRequiredKeyedService<ISettingsBuilder>("one").DefaultSchema);
        Assert.Equal("shared", sp.GetRequiredKeyedService<ISettingsBuilder>("two").DefaultSchema);
    }

    // ---------- key validation ----------

    [Fact]
    public void Register_NullKey_Throws()
    {
        ServiceCollection services = CreateServices();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddSaPartitional((string)null!, (_, _) => { }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_BlankKey_Throws(string key)
    {
        ServiceCollection services = CreateServices();
        Assert.Throws<ArgumentException>(() =>
            services.AddSaPartitional(key, (_, _) => { }));
    }

    [Fact]
    public void Register_ReservedDefaultKey_Throws()
    {
        ServiceCollection services = CreateServices();
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            services.AddSaPartitional(Sa.Schedule.Setup.DefaultName, (_, _) => { }));

        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_NullConfigure_Throws()
    {
        ServiceCollection services = CreateServices();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddSaPartitional("one", (Action<IServiceProvider, ISettingsBuilder>)null!));
    }
}
