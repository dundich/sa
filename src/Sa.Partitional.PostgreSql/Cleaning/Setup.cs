using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Cleaning;

internal static class Setup
{
    readonly static Guid JobId = Guid.Parse("7da81411-9db7-4553-8e93-bd1f12d02b38");

    public static IServiceCollection AddPartCleaning(this IServiceCollection services, Action<IServiceProvider, PartCleanupScheduleSettings>? configure = null)
    {

        if (configure != null)
        {
            services.AddSingleton(configure);
        }

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

        services.AddSchedule(b => b
            .UseHostedService()
            .AddJob<PartCleanupJob>((sp, builder) =>
            {
                builder.WithName("Cleanup job");

                var settings = sp.GetRequiredService<PartCleanupScheduleSettings>();

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
