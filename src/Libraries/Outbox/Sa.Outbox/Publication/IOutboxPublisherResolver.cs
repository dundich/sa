using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.Publication;

/// <summary>
/// Resolves the <see cref="IOutboxMessagePublisher"/> of a named provider. The publisher itself is
/// registered keyed (one per provider key), so a multi-provider application can address each
/// storage explicitly without injecting the container.
/// </summary>
/// <remarks>
/// Equivalent to <c>sp.GetRequiredKeyedService&lt;IOutboxMessagePublisher&gt;(providerKey)</c>, with
/// a clearer error when the key is unknown. The classic single-provider registration keeps an
/// unkeyed publisher alias, so existing code that injects <see cref="IOutboxMessagePublisher"/>
/// directly is unaffected.
/// </remarks>
public interface IOutboxPublisherResolver
{
    /// <summary>Gets the publisher of the provider registered under <paramref name="providerKey"/>.</summary>
    /// <param name="providerKey">The provider key (equal to the outbox scope key).</param>
    /// <returns>The provider's publisher.</returns>
    /// <exception cref="InvalidOperationException">No provider is registered under that key.</exception>
    IOutboxMessagePublisher GetPublisher(string providerKey);
}

internal sealed class OutboxPublisherResolver(IServiceProvider services) : IOutboxPublisherResolver
{
    public IOutboxMessagePublisher GetPublisher(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        return services.GetKeyedService<IOutboxMessagePublisher>(providerKey)
            ?? throw new InvalidOperationException(
                $"No outbox provider is registered under the key '{providerKey}'. " +
                "Register it with AddSaOutboxUsingPostgreSql(providerKey, ...) (or the Redis " +
                "equivalent) and use the same key here.");
    }
}
