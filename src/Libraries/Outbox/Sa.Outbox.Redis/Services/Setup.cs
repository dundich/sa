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

        services
            .RemoveAll<IOutboxBulkWriter>()
            .AddSingleton<IOutboxBulkWriter, OutboxBulkWriter>();

        services
            .RemoveAll<IOutboxDeliveryManager>()
            .AddSingleton<IOutboxDeliveryManager, OutboxDeliveryManager>();

        services
            .RemoveAll<IOutboxTenantDetector>()
            .AddSingleton<IOutboxTenantDetector, OutboxTenantDetector>();

        return services;
    }
}
