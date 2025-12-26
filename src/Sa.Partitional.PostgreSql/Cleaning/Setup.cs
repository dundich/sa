using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Cleaning;

internal static class Setup
{
    readonly static Guid JobId = Guid.Parse("7da81411-9db7-4553-8e93-bd1f12d02b38");

    private static readonly ConcurrentDictionary<IServiceCollection,
        HashSet<Action<IServiceProvider, PartCleanupScheduleSettings>>> s_invokers = [];

    public static IServiceCollection AddPartCleaning(this IServiceCollection services, Action<IServiceProvider, PartCleanupScheduleSettings>? configure = null)
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

        services.TryAddSingleton<PartCleanupScheduleSettings>(sp =>
        {
            var item = new PartCleanupScheduleSettings();
            if (s_invokers.TryGetValue(services, out var invokers))
            {
                foreach (Action<IServiceProvider, PartCleanupScheduleSettings> invoker in invokers)
                {
                    invoker.Invoke(sp, item);
                }
                s_invokers.Remove(services, out _);
            }
            return item;
        });

        services.TryAddSingleton<IPartCleanupService, PartCleanupService>();

        services.AddSchedule(b => b
            .UseHostedService()
            .AddJob<PartCleanupJob>((sp, builder) =>
            {
                builder.WithName("Cleanup job");

                PartCleanupScheduleSettings settings = sp.GetRequiredService<PartCleanupScheduleSettings>();

                builder
                    .WithInitialDelay(settings.InitialDelay)
                    .EveryTime(settings.ExecutionInterval)
                    .ConfigureErrorHandling(berr => berr.DoSuppressError(err => true))
                ;

                if (!settings.AsBackgroundJob)
                {
                    builder.Disabled();
                }

            }, JobId)
        );

        return services;
    }
}
