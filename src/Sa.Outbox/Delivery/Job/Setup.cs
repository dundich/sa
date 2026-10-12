using Microsoft.Extensions.DependencyInjection;
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
            Guid? jobId = null,
            string scopeKey = OutboxScope.DefaultKey,
            string? configurationKey = null)
                where TConsumer : class, IConsumer<TMessage>
    {

        ArgumentNullException.ThrowIfNullOrWhiteSpace(consumerGroupId);

        // Settings are registered in two stages (see DeliveryGroupSettingsRegistration):
        //   1. Registration time — built eagerly from OutboxDefaults and the user configure
        //      callback (called with a null provider, as before). The configuration section
        //      cannot be consulted here: IConfiguration needs a live provider. The consumer
        //      manager is pre-populated with this snapshot, so a group that never starts the
        //      scheduler (manual IDeliveryProcessor.ProcessMessages) still resolves its settings,
        //      and code that captures the built settings inside configure keeps working.
        //   2. Startup — the manager's own factory rebuilds the full chain (OutboxDefaults →
        //      configuration section, when a key is given → user configure with a live provider)
        //      and swaps the snapshot to this result, so the configuration section lands in the
        //      actually-scheduled job.
        //
        // In both stages the consumer group id is re-applied from the registration parameter as
        // the final step: identity (the consumer DI key, the settings-registry key and the job
        // name) is the parameter, not whatever the user configure callback may set, so the two
        // stages — and the consumer the delivery resolves at runtime — always agree.
        OutboxConsumerSettings registrationSettings = BuildSettings(null);

        // The consumer's DI lifetime is the registration identity and cannot be shaped by
        // configuration: AddDelivery = keyed singleton, AddDeliveryScoped = keyed scoped.
        // (The settings' AsSingleton flag mirrors this and is deliberately not a tunable.)
        Registered<TConsumer, TMessage>(services, isSingleton, consumerGroupId);

        services.AddSingleton(new DeliveryGroupSettingsRegistration(
            consumerGroupId,
            registrationSettings,
            sp => BuildSettings(sp)));

        services.AddSaSchedule(scopeKey, builder =>
        {
            builder.UseHostedService();

            builder.AddJob<DeliveryJob<TMessage>>((sp, jobBuilder) =>
            {
                // Resolving the manager runs its factory, which has already upgraded this group's
                // snapshot to the full chain (with a live provider), so the job simply reads the
                // final settings — it does not rebuild or re-register them.
                var manager = sp.GetRequiredService<IOutboxConsumerManager>();
                OutboxConsumerSettings settings = manager.Get(consumerGroupId)
                    ?? throw new InvalidOperationException(
                        $"No OutboxConsumerSettings for consumer group '{consumerGroupId}'.");

                // Timing is adaptive: the job reports each run's outcome into this state and the
                // timing reads it back here. Fixed-interval behavior is preserved when
                // IdleMaxInterval is zero.
                var idleStates = sp.GetRequiredService<IOutboxIdleStateRegistry>();
                OutboxIdleState idleState = idleStates.GetOrAdd(
                    consumerGroupId,
                    settings.Interval,
                    settings.IdleMaxInterval);

                jobBuilder
                    .WithTiming(new AdaptiveIdleTiming(idleState))
                    .WithInitialDelay(settings.InitialDelay)
                    .WithConcurrencyLimit(settings.ConcurrencyLimit)
                    .WithMaxConcurrency(settings.MaxConcurrency)
                    .WithName(consumerGroupId)
                    .ConfigureErrorHandling(c => c
                        .IfErrorRetry(settings.RetryCountOnError)
                        .ThenCloseApplication())
                    ;

            }, jobId);

            builder.AddInterceptor<DeliveryJobInterceptor>();
        });

        return services;

        OutboxConsumerSettings BuildSettings(IServiceProvider? sp)
        {
            // Precedence, lowest → highest:
            //   1. OutboxDefaults — a fresh builder carries them (Build falls back per field)
            //   2. the group's configuration section (stage 2 only, when configurationKey is given)
            //   3. the user configure callback
            var builder = new OutboxConsumerSettingsBuilder()
                .WithConsumerGroupId(consumerGroupId)
                .AsSingleton(isSingleton);

            if (sp is not null)
            {
                var provider = sp.GetRequiredService<IDeliveryGroupOptionsProvider>();
                provider.TryApply(consumerGroupId, configurationKey, builder);
            }

            configure?.Invoke(sp ?? default!, builder);

            // The id is identity, not tuning: pin it back to the parameter so the consumer DI key,
            // the settings-registry key and the job name always match, whatever configure did.
            return builder
                .WithConsumerGroupId(consumerGroupId)
                .Build();
        }
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
