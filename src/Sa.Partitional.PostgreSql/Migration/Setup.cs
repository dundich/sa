using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Migration;

internal static class Setup
{
    private static readonly ConcurrentDictionary<IServiceCollection, HashSet<Action<IServiceProvider, MigrationScheduleSettings>>> s_invokers = [];

    public static IServiceCollection AddMigration(this IServiceCollection services, Action<IServiceProvider, MigrationScheduleSettings>? configure = null)
    {

        if (configure != null)
        {
            if (s_invokers.TryGetValue(services, out var builder))
            {
                builder.Add(configure);
            }
            else
            {
                s_invokers[services] = [configure];
            }
        }

        services.TryAddSingleton<MigrationScheduleSettings>(sp =>
        {
            var item = new MigrationScheduleSettings();
            if (s_invokers.TryGetValue(services, out var invokers))
            {
                foreach (Action<IServiceProvider, MigrationScheduleSettings> invoker in invokers)
                {
                    invoker.Invoke(sp, item);
                }
                s_invokers.Remove(services, out _);
            }
            return item;
        });

        services.TryAddSingleton<IMigrationService, PartMigrationService>();


        services.AddSchedule(b => b
            .UseHostedService()
            .AddJob<MigrationJob>((sp, builder) =>
            {
                MigrationScheduleSettings migrationSettings = sp.GetRequiredService<MigrationScheduleSettings>();

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
