using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Support;
using Sa.Schedule;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Job;

internal static class Setup
{
    public static IServiceCollection AddDeliveryJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        this IServiceCollection services,
        string consumerGroupId,
        Action<IServiceProvider, OutboxDeliverySettings>? сonfigure = null)
            where TConsumer : class, IConsumer<TMessage>
            where TMessage : IOutboxPayloadMessage
    {

        var settings = new OutboxDeliverySettings(consumerGroupId);

        services.AddKeyedScoped<IConsumer<TMessage>, TConsumer>(settings.ConsumeSettings);

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
