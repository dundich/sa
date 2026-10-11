using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sa.Partitional.PostgreSql.Cache;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Migration;

internal static class Setup
{
    public static IServiceCollection AddMigration(
        this IServiceCollection services,
        string? key = null,
        Action<IServiceProvider, MigrationScheduleSettings>? configure = null)
    {
        if (configure != null)
        {
            if (key is null) services.AddSingleton(configure);
            else services.AddKeyedSingleton(key, configure);
        }

        if (key is null)
        {
            services.TryAddSingleton<MigrationScheduleSettings>(sp =>
            {
                var settings = new MigrationScheduleSettings();

                var configurators = sp.GetServices<Action<IServiceProvider, MigrationScheduleSettings>>();

                foreach (var config in configurators)
                {
                    config(sp, settings);
                }

                return settings;
            });

            services.TryAddSingleton<IMigrationService, PartMigrationService>();
        }
        else
        {
            services.TryAddKeyedSingleton<MigrationScheduleSettings>(key, (sp, _) =>
            {
                var settings = new MigrationScheduleSettings();

                foreach (var config in sp.GetKeyedServices<Action<IServiceProvider, MigrationScheduleSettings>>(key))
                {
                    config(sp, settings);
                }

                return settings;
            });

            services.TryAddKeyedSingleton<IMigrationService>(key, (sp, _) => new PartMigrationService(
                sp.GetRequiredKeyedService<IPartRepository>(key),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredKeyedService<MigrationScheduleSettings>(key),
                sp.GetRequiredService<ILogger<PartMigrationService>>()));
        }

        RegisterScheduleJob(services, key);

        return services;
    }

    private static void RegisterScheduleJob(IServiceCollection services, string? key)
    {
        if (key is null)
        {
            services.AddSaSchedule(b => b
                .UseHostedService()
                .AddJob<MigrationJob>((sp, builder) =>
                {
                    var migrationSettings = sp.GetRequiredService<MigrationScheduleSettings>();

                    builder.WithName(migrationSettings.MigrationJobName ?? MigrationJobConstance.MigrationDefaultJobName);

                    builder
                        .StartImmediate()
                        .EveryTime(migrationSettings.ExecutionInterval)
                        // A started run is bounded by its own RunTimeout, so the scheduler has to be
                        // able to drain it: give it a little more than the run itself needs, and keep
                        // both comfortably below the host shutdown timeout.
                        .WithShutdownTimeout(migrationSettings.RunTimeout + TimeSpan.FromSeconds(5))
                        // M5: only the expected stop is suppressed. A real failure (a name over the
                        // 63-byte identifier limit, a broken table definition) is retried, logged and
                        // then aborts THIS job - the scheduler default would close the application.
                        .ConfigureErrorHandling(berr => berr
                            .DoSuppressError(err => err is OperationCanceledException or TimeoutException)
                            .ThenAbortJob())
                    ;

                    if (!migrationSettings.AsBackgroundJob)
                    {
                        builder.Disabled();
                    }

                }, MigrationJobConstance.MigrationJobId)
            );
            return;
        }

        // Keyed module: identical job wiring, but the job lives in the module's own named
        // schedule and carries a module-derived id/name. Registered only on the first call per
        // key: a repeated AddSaPartitional(key, ...) call merges the settings but must not add
        // a second migration job (both would run and both would fight over the same lock).
        Guid jobId = JobKeys.MigrationJobId(key);

        bool already = services.Any(d =>
            d.ServiceType == typeof(MigrationScheduleMarker)
            && Equals(d.ServiceKey, key));

        services.TryAddKeyedSingleton(key, new MigrationScheduleMarker());

        if (already) return;

        // The job instance is constructed here, closing over the module key, so the keyed
        // MigrationJob resolves this module's keyed services. Registered before AddSaSchedule:
        // its internal TryAddKeyedScoped<MigrationJob>(jobId) then sees this registration and
        // leaves it in place instead of replacing it with a bare DI resolve (which would look
        // for an unkeyed IMigrationService/IPartCache and fail).
        services.AddKeyedScoped(jobId, (sp, _) => new MigrationJob(
            sp.GetRequiredKeyedService<IMigrationService>(key),
            sp.GetRequiredKeyedService<IPartCache>(key)));

        services.AddSaSchedule(key, b => b
            .UseHostedService()
            .AddJob<MigrationJob>((sp, builder) =>
            {
                var migrationSettings = sp.GetRequiredKeyedService<MigrationScheduleSettings>(key);

                builder.WithName(migrationSettings.MigrationJobName ?? JobKeys.MigrationJobName(key));

                builder
                    .StartImmediate()
                    .EveryTime(migrationSettings.ExecutionInterval)
                    .WithShutdownTimeout(migrationSettings.RunTimeout + TimeSpan.FromSeconds(5))
                    .ConfigureErrorHandling(berr => berr
                        .DoSuppressError(err => err is OperationCanceledException or TimeoutException)
                        .ThenAbortJob())
                ;

                if (!migrationSettings.AsBackgroundJob)
                {
                    builder.Disabled();
                }

            }, jobId)
        );
    }
}

/// <summary>
/// Per-key sentinel guarding the migration job registration of a keyed module: a repeated
/// <c>AddSaPartitional(key, ...)</c> call must merge settings but not duplicate the job.
/// </summary>
internal sealed class MigrationScheduleMarker
{
}
