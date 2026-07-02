using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;

namespace Sa.Outbox;

public static class Setup
{
    /// <summary>
    /// Registers the Sa.Outbox infrastructure in the service collection and optionally configures it via a builder action.
    /// </summary>
    /// <param name="services">The service collection to add outbox services to.</param>
    /// <param name="build">An optional action to configure the outbox through <see cref="IOutboxBuilder"/>.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
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
