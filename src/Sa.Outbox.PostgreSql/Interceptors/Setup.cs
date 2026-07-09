using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.PostgreSql.Interceptors;

internal static class Setup
{
    public static IServiceCollection AddOutboxJobInterceptors(this IServiceCollection services)
    {
        services.TryAddSingleton<IOutboxDeliveryJobInterceptor, DeliveryJobInterceptor>();
        return services;
    }
}
