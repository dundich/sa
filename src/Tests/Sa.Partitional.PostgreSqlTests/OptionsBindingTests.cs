using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sa.Data.PostgreSql;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests;

/// <summary>
/// The <c>Microsoft.Extensions.Options</c> pipeline behind the scalar settings
/// (<see cref="PartCacheSettings"/>, <see cref="MigrationScheduleSettings"/>,
/// <see cref="PartCleanupScheduleSettings"/>): <c>FromConfiguration</c> binds a section, the code
/// <c>Configure</c> delegates beat it, the <c>Options(...)</c> escape hatch beats both, validation
/// fails fast, and the order is fixed no matter how the registration calls are split across the
/// fluent chain.
/// </summary>
/// <remarks>
/// No database is started: only the settings are resolved, and nothing on that path opens a
/// connection. PostgreSQL-backed scenarios live in the Testcontainers integration tests.
/// </remarks>
public sealed class OptionsBindingTests
{
    private const string ValidConnectionString =
        "Host=127.0.0.1;Database=postgres;Username=u;Password=p;Search Path=public";

    private static ServiceCollection NewServices(IDictionary<string, string?>? entries = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        if (entries is not null)
        {
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(entries).Build());
        }

        return services;
    }

    private static void AddDefaultDataSource(IServiceCollection services)
        => services.AddSaPostgreSqlDataSource(
            o => o.Options(ob => ob.Configure(x => x.ConnectionString = ValidConnectionString)));

    private static IPartConfiguration AddModule(IServiceCollection services, bool? asBackgroundJob = null)
    {
        AddDefaultDataSource(services);

        return services.AddSaPartitional((_, builder) => builder.AddSchema(schema => schema
            .AddTable("t_part", "id bigint NOT NULL", "created_at timestamptz NOT NULL")
            .PartByRange(PgPartBy.Day)), asBackgroundJob);
    }

    private static IPartConfiguration AddKeyedModule(IServiceCollection services, string key)
        => services.AddSaPartitional(key, (_, builder) => builder.AddSchema(schema => schema
            .AddTable("t_part", "id bigint NOT NULL", "created_at timestamptz NOT NULL")
            .PartByRange(PgPartBy.Day)));

    // ---------- defaults and section binding ----------

    [Fact]
    public void NoConfiguration_UsesClassDefaults()
    {
        ServiceCollection services = NewServices();
        AddModule(services);

        using ServiceProvider sp = services.BuildServiceProvider();

        PartCacheSettings cache = sp.GetRequiredService<PartCacheSettings>();
        Assert.Equal(TimeSpan.FromMinutes(5), cache.CacheTtl);
        Assert.Equal(TimeSpan.FromDays(1), cache.CachedFromDate);

        MigrationScheduleSettings migration = sp.GetRequiredService<MigrationScheduleSettings>();
        Assert.Equal(2, migration.ForwardDays);
        Assert.Equal(TimeSpan.FromSeconds(3), migration.WaitMigrationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), migration.RunTimeout);

        PartCleanupScheduleSettings cleanup = sp.GetRequiredService<PartCleanupScheduleSettings>();
        Assert.Equal(TimeSpan.FromDays(30), cleanup.DropPartsAfterRetention);
        Assert.Equal(TimeSpan.FromMinutes(1), cleanup.InitialDelay);
    }

    [Fact]
    public void FromConfiguration_BindsEachSection()
    {
        ServiceCollection services = NewServices(new Dictionary<string, string?>
        {
            ["Partitional:Cache:CacheTtl"] = "00:10:00",
            ["Partitional:Cache:CachedFromDate"] = "2.00:00:00",
            ["Partitional:Migration:ForwardDays"] = "5",
            ["Partitional:Migration:RunTimeout"] = "00:00:20",
            ["Partitional:Cleanup:DropPartsAfterRetention"] = "10.00:00:00",
            ["Partitional:Cleanup:InitialDelay"] = "00:05:00",
        });

        AddModule(services)
            .AddPartCache(b => b.FromConfiguration("Partitional:Cache"))
            .AddPartMigrationSchedule(b => b.FromConfiguration("Partitional:Migration"))
            .AddPartCleanupSchedule(b => b.FromConfiguration("Partitional:Cleanup"));

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(10), sp.GetRequiredService<PartCacheSettings>().CacheTtl);
        Assert.Equal(TimeSpan.FromDays(2), sp.GetRequiredService<PartCacheSettings>().CachedFromDate);
        Assert.Equal(5, sp.GetRequiredService<MigrationScheduleSettings>().ForwardDays);
        Assert.Equal(TimeSpan.FromSeconds(20), sp.GetRequiredService<MigrationScheduleSettings>().RunTimeout);
        Assert.Equal(TimeSpan.FromDays(10), sp.GetRequiredService<PartCleanupScheduleSettings>().DropPartsAfterRetention);
        Assert.Equal(TimeSpan.FromMinutes(5), sp.GetRequiredService<PartCleanupScheduleSettings>().InitialDelay);
    }

    [Fact]
    public void NoFromConfiguration_IgnoresMatchingSection()
    {
        ServiceCollection services = NewServices(new Dictionary<string, string?>
        {
            ["Partitional:Cache:CacheTtl"] = "00:30:00",
        });

        // The module never asks for a section, so the key sits unused and the class default stands.
        AddModule(services);

        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromMinutes(5), sp.GetRequiredService<PartCacheSettings>().CacheTtl);
    }

    // ---------- precedence ----------

    [Fact]
    public void CodeConfigure_BeatsConfiguration()
    {
        ServiceCollection services = NewServices(new Dictionary<string, string?>
        {
            ["Partitional:Cache:CacheTtl"] = "00:10:00",
        });

        AddModule(services).AddPartCache(b => b
            .FromConfiguration("Partitional:Cache")
            .Configure((_, s) => s.CacheTtl = TimeSpan.FromMinutes(1)));

        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromMinutes(1), sp.GetRequiredService<PartCacheSettings>().CacheTtl);
    }

    [Fact]
    public void OptionsActions_HaveTheFinalWord()
    {
        ServiceCollection services = NewServices(new Dictionary<string, string?>
        {
            ["Partitional:Cache:CacheTtl"] = "00:30:00",
        });

        AddModule(services).AddPartCache(b => b
            .FromConfiguration("Partitional:Cache")
            .Configure((_, s) => s.CacheTtl = TimeSpan.FromMinutes(1))
            .Options(ob => ob.Configure(s => s.CacheTtl = TimeSpan.FromMinutes(20))));

        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromMinutes(20), sp.GetRequiredService<PartCacheSettings>().CacheTtl);
    }

    [Fact]
    public void TwoFromConfigurationCalls_LastWins()
    {
        ServiceCollection services = NewServices(new Dictionary<string, string?>
        {
            ["SectionA:CacheTtl"] = "00:01:00",
            ["SectionB:CacheTtl"] = "00:09:00",
        });

        AddModule(services).AddPartCache(b => b
            .FromConfiguration("SectionA")
            .FromConfiguration("SectionB"));

        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromMinutes(9), sp.GetRequiredService<PartCacheSettings>().CacheTtl);
    }

    [Fact]
    public void Precedence_IsStable_WhenConfigAndCodeArriveInSeparateCalls()
    {
        ServiceCollection services = NewServices(new Dictionary<string, string?>
        {
            ["Partitional:Cache:CacheTtl"] = "00:30:00",
        });

        IPartConfiguration module = AddModule(services);

        // The section is registered by one chained call, the code setting by a later one: the fixed
        // pipeline order still lets the code setting win.
        module.AddPartCache(b => b.FromConfiguration("Partitional:Cache"));
        module.AddPartCache(b => b.Configure((_, s) => s.CacheTtl = TimeSpan.FromMinutes(1)));

        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromMinutes(1), sp.GetRequiredService<PartCacheSettings>().CacheTtl);
    }

    [Fact]
    public void AsBackgroundJobArgument_BeatsConfiguration_EvenWhenConfigIsChainedLater()
    {
        ServiceCollection services = NewServices(new Dictionary<string, string?>
        {
            ["Partitional:Migration:AsBackgroundJob"] = "true",
            ["Partitional:Migration:ForwardDays"] = "4",
            ["Partitional:Cleanup:AsBackgroundJob"] = "true",
        });

        // The framework records the explicit argument before the caller chains FromConfiguration.
        IPartConfiguration module = AddModule(services, asBackgroundJob: false);
        module.AddPartMigrationSchedule(b => b.FromConfiguration("Partitional:Migration"));
        module.AddPartCleanupSchedule(b => b.FromConfiguration("Partitional:Cleanup"));

        using ServiceProvider sp = services.BuildServiceProvider();

        MigrationScheduleSettings migration = sp.GetRequiredService<MigrationScheduleSettings>();
        Assert.False(migration.AsBackgroundJob);   // the explicit argument wins
        Assert.Equal(4, migration.ForwardDays);    // the section still fills the keys code left alone
        Assert.False(sp.GetRequiredService<PartCleanupScheduleSettings>().AsBackgroundJob);
    }

    // ---------- validation ----------

    [Fact]
    public void InvalidCodeValue_FailsOnRead()
    {
        ServiceCollection services = NewServices();
        AddModule(services).AddPartMigrationSchedule(b => b.Configure((_, s) => s.ForwardDays = 0));

        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<MigrationScheduleSettings>());
        Assert.Contains("ForwardDays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidSection_FailsValidateOnStart()
    {
        ServiceCollection services = NewServices(new Dictionary<string, string?>
        {
            ["Partitional:Cleanup:ExecutionInterval"] = "00:00:00",
        });

        AddModule(services).AddPartCleanupSchedule(b => b.FromConfiguration("Partitional:Cleanup"));

        using ServiceProvider sp = services.BuildServiceProvider();

        IStartupValidator validator = sp.GetRequiredService<IStartupValidator>();
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => validator.Validate());
        Assert.Contains("ExecutionInterval", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_WithoutRegisteredConfiguration_Throws()
    {
        ServiceCollection services = NewServices();
        AddModule(services).AddPartCache(b => b.FromConfiguration("Partitional:Cache"));

        using ServiceProvider sp = services.BuildServiceProvider();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<PartCacheSettings>());
        Assert.Contains("IConfiguration", ex.Message, StringComparison.Ordinal);
    }

    // ---------- keyed modules ----------

    [Fact]
    public void KeyedModules_BindTheirOwnSections()
    {
        ServiceCollection services = NewServices(new Dictionary<string, string?>
        {
            ["Partitional:one:Cache:CacheTtl"] = "00:01:00",
            ["Partitional:two:Cache:CacheTtl"] = "00:09:00",
            ["Partitional:one:Migration:ForwardDays"] = "3",
            ["Partitional:two:Migration:ForwardDays"] = "8",
        });

        AddDefaultDataSource(services);

        AddKeyedModule(services, "one")
            .AddPartCache(b => b.FromConfiguration("Partitional:one:Cache"))
            .AddPartMigrationSchedule(b => b.FromConfiguration("Partitional:one:Migration"));

        AddKeyedModule(services, "two")
            .AddPartCache(b => b.FromConfiguration("Partitional:two:Cache"))
            .AddPartMigrationSchedule(b => b.FromConfiguration("Partitional:two:Migration"));

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(1), sp.GetRequiredKeyedService<PartCacheSettings>("one").CacheTtl);
        Assert.Equal(TimeSpan.FromMinutes(9), sp.GetRequiredKeyedService<PartCacheSettings>("two").CacheTtl);
        Assert.Equal(3, sp.GetRequiredKeyedService<MigrationScheduleSettings>("one").ForwardDays);
        Assert.Equal(8, sp.GetRequiredKeyedService<MigrationScheduleSettings>("two").ForwardDays);

        // the keyless module was never registered
        Assert.Null(sp.GetService<PartCacheSettings>());
    }
}
