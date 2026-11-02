using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Redis.Commands;
using Sa.Outbox.Redis.Poison;
using Sa.Outbox.Redis.Services.Plug;

namespace Sa.Outbox.Redis.Services;

internal static class Setup
{
    public static IServiceCollection AddOutboxServices(this IServiceCollection services)
    {
        services.TryAddSingleton<PublishMessagesCommand>();
        services.TryAddSingleton<RedisOutboxGroupRegistry>();
        services.TryAddSingleton<RedisOutboxTrimmer>();
        services.TryAddSingleton<RentDeliveryCommand>();
        services.TryAddSingleton<FinishDeliveryCommand>();
        services.TryAddSingleton<ExtendDeliveryCommand>();

        // The provider keeps no dead-letter stream: poisoned messages go to this handler. The
        // default drops them with a warning; WithPoisonHandler replaces it.
        services.TryAddSingleton<IRedisOutboxPoisonHandler, DropPoisonHandler>();

        // Identity, capabilities and the three storage contracts in one place. Redis has no
        // publish context (publishing is append-only into a stream) and ignores a consumer-supplied
        // Postpone/Retry delay (the only retry clock is the PEL idle time), so neither capability
        // is declared; lock extension is honoured through ExtendDelivery.
        services.AddOutboxProvider(
            new OutboxProviderDescriptor(
                "Redis",
                OutboxProviderCapabilities.TenantDetection
                | OutboxProviderCapabilities.LockExtension),
            provider => provider
                .WithBulkWriter<OutboxBulkWriter>()
                .WithDeliveryManager<OutboxDeliveryManager>()
                .WithTenantDetector<OutboxTenantDetector>());

        return services;
    }
}
