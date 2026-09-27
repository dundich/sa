using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Migration;

internal static class Setup
{
    public static IServiceCollection AddMigration(
        this IServiceCollection services,
        Action<IServiceProvider, MigrationScheduleSettings>? configure = null)
    {

        if (configure != null)
        {
            services.AddSingleton(configure);
        }

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

        return services;
    }
}
