using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Interceptors;

internal static class Setup
{
    public static IServiceCollection AddOutboxJobInterceptors(this IServiceCollection services, string scopeKey)
    {
        // The migration state the interceptor guards on lives in the outbox's keyed partitional
        // module, so resolve that module's IMigrationService.
        services.TryAddSingleton<IOutboxDeliveryJobInterceptor>(sp =>
            new DeliveryJobInterceptor(sp.GetRequiredKeyedService<IMigrationService>(scopeKey)));

        return services;
    }
}
