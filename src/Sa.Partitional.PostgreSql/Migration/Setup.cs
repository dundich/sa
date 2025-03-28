using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Migration;



internal static class Setup
{
    private static readonly ConcurrentDictionary<IServiceCollection, HashSet<Action<IServiceProvider, PartMigrationScheduleSettings>>> s_invokers = [];

    public static IServiceCollection AddPartMigration(this IServiceCollection services, Action<IServiceProvider, PartMigrationScheduleSettings>? configure = null)
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

        services.TryAddSingleton<PartMigrationScheduleSettings>(sp =>
        {
            var item = new PartMigrationScheduleSettings();
            if (s_invokers.TryGetValue(services, out var invokers))
            {
                foreach (Action<IServiceProvider, PartMigrationScheduleSettings> invoker in invokers)
                {
                    invoker.Invoke(sp, item);
                }
                s_invokers.Remove(services, out _);
            }
            return item;
        });

        services.TryAddSingleton<IPartMigrationService, PartMigrationService>();


        services.AddSchedule(b => b
            .UseHostedService()
            .AddJob<PartMigrationJob>((sp, builder) =>
            {
                PartMigrationScheduleSettings migrationSettings = sp.GetRequiredService<PartMigrationScheduleSettings>();

                builder.WithName(migrationSettings.MigrationJobName ?? MigrationJobConstance.MigrationDefaultJobName);

                builder
                    .StartImmediate()
                    .EveryTime(migrationSettings.ExecutionInterval)
                    .ConfigureErrorHandling(berr => berr.DoSuppressError(err => true))
                ;

                if (!migrationSettings.AsJob)
                {
                    builder.Disabled();
                }

            }, MigrationJobConstance.MigrationJobId)
        );

        return services;
    }
}
