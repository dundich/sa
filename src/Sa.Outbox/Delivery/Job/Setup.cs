using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Schedule;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Delivery.Job;

internal static class Setup
{
    public static IServiceCollection AddDeliveryJob<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
            this IServiceCollection services,
            string consumerGroupId,
            bool isSingleton,
            Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
            Guid? jobId = null)
                where TConsumer : class, IConsumer<TMessage>
    {

        ArgumentNullException.ThrowIfNullOrWhiteSpace(consumerGroupId);

        // Build settings from scratch via builder
        var builder = new OutboxConsumerSettingsBuilder();
        builder
            .WithConsumerGroupId(consumerGroupId)
            .AsSingleton(isSingleton)
            .StartImmediately()
            .WithConcurrencyLimit(1)
            .WithMaxConcurrency(1)
            .WithMaxProcessingIterations(-1)
            .WithPerTenantTimeout(TimeSpan.Zero)
            .Paused(false);

        // Allow caller to tweak settings via fluent builder
        configure?.Invoke(default!, builder);

        var settings = builder.Build();

        Registered<TConsumer, TMessage>(services, isSingleton, settings.ConsumerGroupId);

        services.AddSaSchedule(builder =>
        {
            builder.UseHostedService();

            builder.AddJob<DeliveryJob<TMessage>>((sp, jobBuilder) =>
            {
                jobBuilder
                    .EveryTime(settings.Interval)
                    .WithInitialDelay(settings.InitialDelay)
                    .WithTag(settings)
                    .WithConcurrencyLimit(settings.ConcurrencyLimit)
                    .WithMaxConcurrency(settings.MaxConcurrency)
                    .WithName(settings.ConsumerGroupId)
                    .ConfigureErrorHandling(c => c
                        .IfErrorRetry(settings.RetryCountOnError)
                        .ThenCloseApplication())
                    ;

            }, jobId);


            builder.AddInterceptor<OutboxJobInterceptor>();
        });

        services.TryAddSingleton<IDeliveryScheduleProvider, DeliveryScheduleProvider>();

        return services;
    }

    private static void Registered<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        IServiceCollection services,
        bool isSingleton,
        string consumerGroupId) where TConsumer : class, IConsumer<TMessage>
    {
        if (isSingleton)
        {
            services.AddKeyedSingleton<IConsumer<TMessage>, TConsumer>(consumerGroupId);
        }
        else
        {
            services.AddKeyedScoped<IConsumer<TMessage>, TConsumer>(consumerGroupId);
        }
    }
}
