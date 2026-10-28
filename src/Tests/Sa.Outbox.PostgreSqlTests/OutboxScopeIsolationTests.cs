using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Data.PostgreSql;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSqlTests;

/// <summary>
/// The Outbox registers its internal <c>Sa.Schedule</c> schedule and <c>Sa.Partitional.PostgreSql</c>
/// module under its own <see cref="OutboxScope.DefaultKey"/> scope, so an application's own
/// <c>AddSaSchedule()</c> / <c>AddSaPartitional()</c> registrations never share jobs or tables with it.
/// </summary>
/// <remarks>
/// No database is opened: only the service collection is built and the schedule settings are
/// inspected, which never resolves the migration/cleanup jobs themselves.
/// </remarks>
public sealed class OutboxScopeIsolationTests
{
    const string ConnectionString = "Host=127.0.0.1;Database=postgres;Username=u;Password=p";

    sealed class TestMessage
    {
        public string PayloadId { get; set; } = "";
    }

    sealed class TestConsumer : IConsumer<TestMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    static ServiceCollection CreateServices(string scopeKey = OutboxScope.DefaultKey)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddSaOutbox(
            builder => builder
                .WithTenants((_, t) => t.WithTenantIds(1))
                .WithDeliveries(d => d.AddDeliveryScoped<TestConsumer, TestMessage>("cg_scope_test")),
            scopeKey);

        services.AddSaOutboxUsingPostgreSql(
            cfg => cfg.WithDataSource(ds => ds.Options(ob => ob.Configure(
                o => o.ConnectionString = ConnectionString))),
            scopeKey);

        return services;
    }

    static IPartConfiguration AddAppDefaultModule(IServiceCollection services)
        => services.AddSaPartitional(
            (_, builder) => builder.AddSchema(schema => schema
                .AddTable("app_table", "id bigint NOT NULL", "created_at bigint NOT NULL")
                .PartByRange(PgPartBy.Day)),
            asBackgroundJob: true);

    [Fact]
    public void Outbox_WithoutAppRegistrations_DoesNotCreateTheDefaultScheduleOrModule()
    {
        using ServiceProvider sp = CreateServices().BuildServiceProvider();

        // the Outbox scope exists …
        Assert.NotNull(sp.GetRequiredKeyedService<IScheduler>(OutboxScope.DefaultKey));
        Assert.NotNull(sp.GetRequiredKeyedService<IPartitionManager>(OutboxScope.DefaultKey));

        // … and the application's default one was never touched
        Assert.Null(sp.GetService<IScheduler>());
        Assert.Null(sp.GetService<IPartitionManager>());
        Assert.Null(sp.GetService<MigrationScheduleSettings>());
    }

    [Fact]
    public void Outbox_AndAppDefaultModule_KeepTheirOwnJobsAndTables()
    {
        ServiceCollection services = CreateServices();
        AddAppDefaultModule(services);

        using ServiceProvider sp = services.BuildServiceProvider();

        IScheduleSettings outbox = sp.GetRequiredKeyedService<IScheduleSettings>(OutboxScope.DefaultKey);
        IScheduleSettings app = sp.GetRequiredService<IScheduleSettings>();

        // Outbox schedule: migration + cleanup + the one delivery job.
        Assert.Equal(3, outbox.GetJobSettings().Count());
        Assert.Contains(outbox.GetJobSettings(), j => j.Properties.JobName == "cg_scope_test");

        // App default schedule: only its own migration + cleanup.
        Assert.Equal(2, app.GetJobSettings().Count());
        Assert.DoesNotContain(app.GetJobSettings(), j => j.Properties.JobName == "cg_scope_test");

        // Table registries are independent.
        ITableSettingsStorage outboxTables = sp
            .GetRequiredKeyedService<ISettingsBuilder>(OutboxScope.DefaultKey)
            .Build();
        ITableSettingsStorage appTables = sp
            .GetRequiredService<ISettingsBuilder>()
            .Build();

        Assert.DoesNotContain(
            outboxTables.Tables,
            t => t.FullName.EndsWith("app_table", StringComparison.Ordinal));
        Assert.Contains(
            appTables.Tables,
            t => t.FullName.EndsWith("app_table", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomScopeKey_IsHonoredAndTheDefaultKeyStaysUnused()
    {
        const string key = "acme-outbox";

        using ServiceProvider sp = CreateServices(key).BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredKeyedService<IScheduler>(key));
        Assert.NotNull(sp.GetRequiredKeyedService<IPartitionManager>(key));
        Assert.Null(sp.GetKeyedService<IScheduler>(OutboxScope.DefaultKey));
        Assert.Null(sp.GetService<IScheduler>());
    }

    [Fact]
    public void ReservedDefaultKey_IsRejected()
    {
        ServiceCollection services = new();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        Assert.Throws<ArgumentException>(() => services.AddSaOutbox(scopeKey: Sa.Schedule.Setup.DefaultName));
        Assert.Throws<ArgumentException>(() => services.AddSaOutboxUsingPostgreSql(scopeKey: Sa.Schedule.Setup.DefaultName));
    }
}
