using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Partitional.PostgreSql.Cache;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Cleaning;

internal static class Setup
{
    readonly static Guid JobId = Guid.Parse("7da81411-9db7-4553-8e93-bd1f12d02b38");

    public static IServiceCollection AddPartCleaning(
        this IServiceCollection services,
        string? key = null,
        Action<IServiceProvider, PartCleanupScheduleSettings>? configure = null)
    {
        if (configure != null)
        {
            if (key is null) services.AddSingleton(configure);
            else services.AddKeyedSingleton(key, configure);
        }

        if (key is null)
        {
            services.TryAddSingleton<PartCleanupScheduleSettings>(sp =>
            {
                var settings = new PartCleanupScheduleSettings();

                var configurators = sp.GetServices<Action<IServiceProvider, PartCleanupScheduleSettings>>();
                foreach (var config in configurators)
                {
                    config(sp, settings);
                }

                return settings;
            });

            services.TryAddSingleton<IPartCleanupService, PartCleanupService>();
        }
        else
        {
            services.TryAddKeyedSingleton<PartCleanupScheduleSettings>(key, (sp, _) =>
            {
                var settings = new PartCleanupScheduleSettings();

                foreach (var config in sp.GetKeyedServices<Action<IServiceProvider, PartCleanupScheduleSettings>>(key))
                {
                    config(sp, settings);
                }

                return settings;
            });

            services.TryAddKeyedSingleton<IPartCleanupService>(key, (sp, _) => new PartCleanupService(
                sp.GetRequiredKeyedService<IPartRepository>(key),
                sp.GetRequiredKeyedService<PartCleanupScheduleSettings>(key),
                sp.GetRequiredKeyedService<ISqlBuilder>(key),
                sp.GetService<TimeProvider>()));
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
                .AddJob<PartCleanupJob>((sp, builder) =>
                {
                    builder.WithName("Cleanup job");

                    var settings = sp.GetRequiredService<PartCleanupScheduleSettings>();

                    builder
                        .WithInitialDelay(settings.InitialDelay)
                        .EveryTime(settings.ExecutionInterval)
                        // M5: only the expected stop is suppressed. A real failure is retried, logged
                        // and then aborts THIS job (the scheduler default would close the whole
                        // application, which is not a sane reaction to a failing DROP).
                        .ConfigureErrorHandling(berr => berr
                            .DoSuppressError(err => err is OperationCanceledException or TimeoutException)
                            .ThenAbortJob())
                    ;

                    if (!settings.AsBackgroundJob)
                    {
                        builder.Disabled();
                    }

                }, JobId)
            );
            return;
        }

        // Keyed module: see Migration/Setup.cs for the same wiring rationale — the job lives in
        // the module's own named schedule, carries a module-derived id/name and is registered
        // only once per key.
        Guid jobId = JobKeys.CleanupJobId(key);

        bool already = services.Any(d =>
            d.ServiceType == typeof(PartCleanupScheduleMarker)
            && Equals(d.ServiceKey, key));

        services.TryAddKeyedSingleton(key, new PartCleanupScheduleMarker());

        if (already) return;

        services.AddKeyedScoped(jobId, (sp, _) => new PartCleanupJob(
            sp.GetRequiredKeyedService<IPartCleanupService>(key),
            sp.GetRequiredKeyedService<IPartCache>(key)));

        services.AddSaSchedule(key, b => b
            .UseHostedService()
            .AddJob<PartCleanupJob>((sp, builder) =>
            {
                builder.WithName(JobKeys.CleanupJobName(key));

                var settings = sp.GetRequiredKeyedService<PartCleanupScheduleSettings>(key);

                builder
                    .WithInitialDelay(settings.InitialDelay)
                    .EveryTime(settings.ExecutionInterval)
                    .ConfigureErrorHandling(berr => berr
                        .DoSuppressError(err => err is OperationCanceledException or TimeoutException)
                        .ThenAbortJob())
                ;

                if (!settings.AsBackgroundJob)
                {
                    builder.Disabled();
                }

            }, jobId)
        );
    }
}

/// <summary>
/// Per-key sentinel guarding the cleanup job registration of a keyed module: a repeated
/// <c>AddSaPartitional(key, ...)</c> call must merge settings but not duplicate the job.
/// </summary>
internal sealed class PartCleanupScheduleMarker
{
}
