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
                    .ConfigureErrorHandling(berr => berr.DoSuppressError(err => true))
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
