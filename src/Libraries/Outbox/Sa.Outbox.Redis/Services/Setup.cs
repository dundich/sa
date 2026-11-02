using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Redis.Commands;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Redis.Poison;
using Sa.Outbox.Redis.Services.Plug;
using Sa.Outbox.Serialization;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Services;

internal static class Setup
{
    public static IServiceCollection AddOutboxServices(this IServiceCollection services, string providerKey)
    {
        // Every command is keyed by the provider key and resolves the provider's own database and
        // settings. The commands are singletons per provider, as they were per container before.
        services.TryAddKeyedSingleton<RedisOutboxGroupRegistry>(providerKey, (sp, _) =>
            new RedisOutboxGroupRegistry(
                sp.GetRequiredKeyedService<IDatabase>(providerKey),
                sp.GetRequiredKeyedService<RedisOutboxSettings>(providerKey)));

        services.TryAddKeyedSingleton<RedisOutboxTrimmer>(providerKey, (sp, _) =>
            new RedisOutboxTrimmer(
                sp.GetRequiredKeyedService<IDatabase>(providerKey),
                sp.GetRequiredKeyedService<RedisOutboxSettings>(providerKey),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<RedisOutboxTrimmer>>()));

        services.TryAddKeyedSingleton<PublishMessagesCommand>(providerKey, (sp, _) =>
            new PublishMessagesCommand(
                sp.GetRequiredKeyedService<IDatabase>(providerKey),
                sp.GetRequiredKeyedService<RedisOutboxSettings>(providerKey),
                sp.GetRequiredService<IOutboxMessageSerializer>(),
                sp.GetRequiredKeyedService<RedisOutboxTrimmer>(providerKey)));

        services.TryAddKeyedSingleton<RentDeliveryCommand>(providerKey, (sp, _) =>
            new RentDeliveryCommand(
                sp.GetRequiredKeyedService<IDatabase>(providerKey),
                sp.GetRequiredKeyedService<RedisOutboxSettings>(providerKey),
                sp.GetRequiredKeyedService<RedisOutboxGroupRegistry>(providerKey),
                ResolvePoisonHandler(sp, providerKey),
                sp.GetRequiredService<IOutboxContextFactory>(),
                sp.GetRequiredService<IOutboxMessageSerializer>(),
                sp.GetService<ILogger<RentDeliveryCommand>>()));

        services.TryAddKeyedSingleton<FinishDeliveryCommand>(providerKey, (sp, _) =>
            new FinishDeliveryCommand(
                sp.GetRequiredKeyedService<IDatabase>(providerKey),
                sp.GetRequiredKeyedService<RedisOutboxSettings>(providerKey),
                ResolvePoisonHandler(sp, providerKey),
                sp.GetRequiredService<IOutboxMessageSerializer>(),
                sp.GetService<ILogger<FinishDeliveryCommand>>()));

        services.TryAddKeyedSingleton<ExtendDeliveryCommand>(providerKey, (sp, _) =>
            new ExtendDeliveryCommand(
                sp.GetRequiredKeyedService<IDatabase>(providerKey),
                sp.GetRequiredKeyedService<RedisOutboxSettings>(providerKey)));

        // The provider keeps no dead-letter stream: poisoned messages go to this handler. The
        // default drops them with a warning; WithPoisonHandler registers a keyed one per provider
        // and the commands above prefer the keyed registration.
        services.TryAddSingleton<IRedisOutboxPoisonHandler, DropPoisonHandler>();

        // Identity, capabilities and the three storage contracts in one place. Redis has no
        // publish context (publishing is append-only into a stream) and ignores a consumer-supplied
        // Postpone/Retry delay (the only retry clock is the PEL idle time), so neither capability
        // is declared; lock extension is honoured through ExtendDelivery.
        services.AddOutboxProvider(
            providerKey,
            new OutboxProviderDescriptor(
                "Redis",
                OutboxProviderCapabilities.TenantDetection
                | OutboxProviderCapabilities.LockExtension,
                Version: typeof(Setup).Assembly.GetName().Version?.ToString()),
            provider => provider
                .WithBulkWriter(sp => new OutboxBulkWriter(
                    sp.GetRequiredKeyedService<PublishMessagesCommand>(providerKey)))
                .WithDeliveryManager(sp => new OutboxDeliveryManager(
                    sp.GetRequiredKeyedService<RentDeliveryCommand>(providerKey),
                    sp.GetRequiredKeyedService<FinishDeliveryCommand>(providerKey),
                    sp.GetRequiredKeyedService<ExtendDeliveryCommand>(providerKey)))
                .WithTenantDetector(sp => new OutboxTenantDetector(
                    sp.GetRequiredKeyedService<IDatabase>(providerKey),
                    sp.GetRequiredKeyedService<RedisOutboxSettings>(providerKey))));

        return services;
    }

    /// <summary>
    /// Keyed per provider when <c>WithPoisonHandler</c> configured one, otherwise the shared default.
    /// </summary>
    private static IRedisOutboxPoisonHandler ResolvePoisonHandler(IServiceProvider sp, string providerKey)
        => sp.GetKeyedService<IRedisOutboxPoisonHandler>(providerKey)
           ?? sp.GetRequiredService<IRedisOutboxPoisonHandler>();
}
