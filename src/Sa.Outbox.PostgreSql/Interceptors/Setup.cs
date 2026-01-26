using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery.Job;

namespace Sa.Outbox.PostgreSql.Interceptors;

internal static class Setup
{
    public static IServiceCollection AddOutboxJobInterceptors(this IServiceCollection services)
    {
        services.TryAddSingleton<IOutboxJobInterceptor, DeliveryJobInterceptor>();
        return services;
    }
}
