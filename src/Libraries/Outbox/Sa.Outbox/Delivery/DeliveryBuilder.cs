using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Extensions;
using Sa.Outbox.Delivery.Job;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Delivery;

internal sealed class DeliveryBuilder(IServiceCollection services, string scopeKey) : IDeliveryBuilder
{

    private IConsumerGroupNamingStrategy _defaultNamingStrategy = new DefaultConsumerGroupNamingStrategy();

    public IDeliveryBuilder AddDeliveryScoped<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null,
        string? configurationKey = null)
            where TConsumer : class, IConsumer<TMessage>
    {
        EnsureValidConsumerGroupId(consumerGroupId);
        services.AddDeliveryJob<TConsumer, TMessage>(consumerGroupId, false, configure, jobId, scopeKey, configurationKey);
        return this;
    }

    public IDeliveryBuilder AddDeliveryScoped<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>
        (Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null, Guid? jobId = null, string? configurationKey = null)
          where TConsumer : class, IConsumer<TMessage>
    {
        return AddDeliveryScoped<TConsumer, TMessage>(GetConsumerGroupName<TConsumer>(), configure, jobId, configurationKey);
    }

    public IDeliveryBuilder AddDelivery<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null,
        string? configurationKey = null)
            where TConsumer : class, IConsumer<TMessage>
    {
        EnsureValidConsumerGroupId(consumerGroupId);
        services.AddDeliveryJob<TConsumer, TMessage>(consumerGroupId, true, configure, jobId, scopeKey, configurationKey);
        return this;
    }

    public IDeliveryBuilder AddDelivery<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null, Guid? jobId = null, string? configurationKey = null)
          where TConsumer : class, IConsumer<TMessage>
    {
        return AddDelivery<TConsumer, TMessage>(GetConsumerGroupName<TConsumer>(), configure, jobId, configurationKey);
    }

    public IDeliveryBuilder AddDeliveryBatching<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
        where TImplementation : class, IDeliveryBatcher
    {
        services
            .RemoveAll<IDeliveryBatcher>()
            .TryAddSingleton<IDeliveryBatcher, TImplementation>();
        return this;
    }

    public string GetConsumerGroupName<TConsumer>() => _defaultNamingStrategy.GetConsumerGroupName<TConsumer>();

    public IDeliveryBuilder ConfigureDefaultNamingStrategy(IConsumerGroupNamingStrategy strategy)
    {
        _defaultNamingStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    sealed class DefaultConsumerGroupNamingStrategy : IConsumerGroupNamingStrategy
    {
        string IConsumerGroupNamingStrategy.GetConsumerGroupName<TConsumer>()
            => $"cg_{(typeof(TConsumer).FullName ?? typeof(TConsumer).Name).GetMurmurHash3()}";
    }

    /// <summary>
    /// Rejects a consumer group id that is not already a canonical key instead of silently rewriting
    /// it. The id is an identity used verbatim as the consumer's DI key, the settings-registry key,
    /// the job name and part of the PostgreSQL partition name; sanitising it (<c>es-group</c> and
    /// <c>es_group</c> both became <c>es_group</c>, <c>ES_GROUP</c> became <c>es_group</c>) made two
    /// distinct registrations collapse into one, so the caller now gets a loud error.
    /// </summary>
    /// <param name="consumerGroupId">The id supplied to <c>AddDelivery</c>.</param>
    /// <exception cref="ArgumentException">The id is blank or contains a character outside
    /// <c>[a-z0-9_]</c>.</exception>
    internal static void EnsureValidConsumerGroupId(string? consumerGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroupId);

        foreach (char c in consumerGroupId)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
                continue;

            throw new ArgumentException(
                $"Consumer group id '{consumerGroupId}' is invalid: the character '{c}' is not " +
                "allowed. A group id is used verbatim as the consumer's DI key, the settings key, " +
                "the job name and part of the PostgreSQL partition name, so it must contain only " +
                "lowercase ASCII letters, digits and underscores (for example 'es_group').",
                nameof(consumerGroupId));
        }
    }
}
