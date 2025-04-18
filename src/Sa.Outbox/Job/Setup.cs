using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Schedule;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Job;

internal static class Setup
{
    public static IServiceCollection AddDeliveryJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(this IServiceCollection services, Action<IServiceProvider, OutboxDeliverySettings>? сonfigure, int intstanceCount)
         where TConsumer : class, IConsumer<TMessage>
    {
        services.TryAddScoped<IConsumer<TMessage>, TConsumer>();

        if (intstanceCount > 0)
        {
            AddSchedule<TConsumer, TMessage>(services, сonfigure, intstanceCount);
        }

        return services;
    }

    private static void AddSchedule<TConsumer, TMessage>(IServiceCollection services, Action<IServiceProvider, OutboxDeliverySettings>? сonfigure, int intstanceCount)
        where TConsumer : class, IConsumer<TMessage>
    {
        services.AddSchedule(builder =>
        {
            builder.UseHostedService();

            for (int i = 0; i < intstanceCount; i++)
            {
                Guid jobId = Guid.NewGuid();

                builder.AddJob<DeliveryJob<TMessage>>((sp, jobBuilder) =>
                {
                    var settings = new OutboxDeliverySettings(jobId, i);
                    сonfigure?.Invoke(sp, settings);

                    ScheduleSettings scheduleSettings = settings.ScheduleSettings;

                    jobBuilder
                        .EveryTime(scheduleSettings.ExecutionInterval)
                        .WithInitialDelay(scheduleSettings.InitialDelay)
                        .WithTag(settings)
                        .WithName(scheduleSettings.Name ?? typeof(TConsumer).Name)
                        .ConfigureErrorHandling(c => c
                            .IfErrorRetry(scheduleSettings.RetryCountOnError)
                            .ThenCloseApplication())
                        ;

                }, jobId);
            }

            builder.AddInterceptor<OutboxJobInterceptor>();
        });
    }
}
