using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Support;
using Sa.Schedule;

namespace Sa.Outbox.Job;

internal static class Setup
{
    public static IServiceCollection AddDeliveryJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        this IServiceCollection services,
        string consumerGroupId,
        bool isSingleton,
        Action<IServiceProvider, ConsumerGroupSettings>? сonfigure = null)
            where TConsumer : class, IConsumer<TMessage>
            where TMessage : IOutboxPayloadMessage
    {

        ArgumentNullException.ThrowIfNullOrWhiteSpace(consumerGroupId);

        ConsumerGroupSettings settings = new(consumerGroupId, isSingleton);

        if (isSingleton)
        {
            services.AddKeyedSingleton<IConsumer<TMessage>, TConsumer>(settings);
        }
        else
        {
            services.AddKeyedScoped<IConsumer<TMessage>, TConsumer>(settings);
        }

        services.AddSchedule(builder =>
        {
            builder.UseHostedService();

            builder.AddJob<DeliveryJob<TMessage>>((sp, jobBuilder) =>
            {
                сonfigure?.Invoke(sp, settings);

                ScheduleSettings scheduleSettings = settings.ScheduleSettings;

                jobBuilder
                    .EveryTime(scheduleSettings.Interval)
                    .WithInitialDelay(scheduleSettings.InitialDelay)
                    .WithTag(settings)
                    .WithName(scheduleSettings.Name ?? typeof(TConsumer).Name)
                    .ConfigureErrorHandling(c => c
                        .IfErrorRetry(scheduleSettings.RetryCountOnError)
                        .ThenCloseApplication())
                    ;

            }, settings.ScheduleSettings.JobId);


            builder.AddInterceptor<OutboxJobInterceptor>();
        });

        return services;
    }
}
