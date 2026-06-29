using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery.Job;
using Sa.Schedule;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Delivery.Job;

internal static class Setup
{
    /// <summary>
    /// Queue of settings registered via AddDeliveryJob, consumed by DeliverySnapshot.
    /// Thread-safe for bootstrap phase only.
    /// </summary>
    internal static readonly Queue<OutboxConsumerSettings?> RegisteredSettings = new();

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
            .WithInterval(TimeSpan.FromMinutes(1))
            .StartImmediately()
            .WithConcurrencyLimit(1)
            .WithMaxConcurrency(1)
            .WithRetryCountOnError(3)
            .WithMaxBatchSize(16)
            .WithMaxProcessingIterations(-1)
            .WithIterationDelay(TimeSpan.Zero)
            .WithLockDuration(TimeSpan.FromSeconds(10))
            .WithLockRenewal(TimeSpan.FromSeconds(3))
            .WithLookbackInterval(TimeSpan.FromDays(7))
            .WithMaxDeliveryAttempts(3)
            .WithBatchingWindow(TimeSpan.FromSeconds(3))
            .WithPerTenantTimeout(TimeSpan.Zero)
            .WithPerTenantMaxDegreeOfParallelism(1)
            .Paused(false);

        // Allow caller to tweak settings via fluent builder
        configure?.Invoke(default!, builder);

        var settings = builder.Build();

        // Register in the static registry for DeliverySnapshot
        RegisteredSettings.Enqueue(settings);

        if (isSingleton)
        {
            services.AddKeyedSingleton<IConsumer<TMessage>, TConsumer>(settings);
        }
        else
        {
            services.AddKeyedScoped<IConsumer<TMessage>, TConsumer>(settings);
        }

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

            }, jobId ?? Guid.Empty);


            builder.AddInterceptor<OutboxJobInterceptor>();
        });

        services.TryAddSingleton<IDeliveryScheduleProvider, DeliveryScheduleProvider>();

        return services;
    }
}
