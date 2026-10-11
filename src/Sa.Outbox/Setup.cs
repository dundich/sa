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
    /// <param name="scopeKey">
    /// The dependency-injection scope of the outbox — the key under which its internal schedule
    /// (delivery, migration and cleanup jobs) is registered. Defaults to
    /// <see cref="OutboxScope.DefaultKey"/>. The Outbox never uses the application's default
    /// schedule; when a provider package such as <c>AddSaOutboxUsingPostgreSql</c> is used, pass
    /// the same value to both so the outbox partitional module and its schedule agree.
    /// </param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty,
    /// whitespace, or <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    public static IServiceCollection AddSaOutbox(
        this IServiceCollection services,
        Action<IOutboxBuilder>? build = null,
        string scopeKey = OutboxScope.DefaultKey)
    {
        OutboxScope.EnsureValid(scopeKey);

        OutboxBuilder builder = OutboxBuilder.Create(services, scopeKey);
        build?.Invoke(builder);

        services.TryAddSingleton<IRetryStrategy, ExponentialBackoffRetryStrategy>();

        return services;
    }
}
