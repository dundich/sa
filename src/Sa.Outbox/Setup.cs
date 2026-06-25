using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;

namespace Sa.Outbox;

public static class Setup
{
    public static IServiceCollection AddSaOutbox(
        this IServiceCollection services,
        Action<IOutboxBuilder>? build = null)
    {
        OutboxBuilder builder = OutboxBuilder.Create(services);
        build?.Invoke(builder);

        services.TryAddSingleton<IRetryStrategy, ExponentialBackoffRetryStrategy>();

        return services;
    }
}
