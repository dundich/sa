using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.Metadata;
using Sa.Outbox.Partitional;
using Sa.Outbox.Publication;
using Sa.Outbox.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox;

internal sealed class OutboxBuilder : IOutboxBuilder
{
    private readonly IServiceCollection _services;

    /// <summary>The DI scope key the outbox schedule (delivery jobs) is registered under.</summary>
    private readonly string _scopeKey;

    private OutboxBuilder(IServiceCollection services, string scopeKey)
    {
        _services = services;
        _scopeKey = scopeKey;
    }

    public static OutboxBuilder Create(IServiceCollection services, string scopeKey)
    {
        // The shared serialization strategy: a JSON fallback every provider resolves unless the
        // application registers its own serializer (a provider's WithMessageSerializer does exactly
        // that). TryAdd keeps a serializer registered earlier — before AddSaOutbox or by a provider
        // configured ahead of it — winning over the default.
        services.TryAddSingleton<IOutboxMessageSerializer>(JsonOutboxMessageSerializer.Instance);

        services
            .AddMessagePublisher(scopeKey: scopeKey)
            .AddOutboxDelivery(scopeKey: scopeKey);

        return new OutboxBuilder(services, scopeKey);
    }

    public IOutboxBuilder WithPublishSettings(Action<IServiceProvider, OutboxPublishSettings> configure)
    {
        _services.AddMessagePublisher(configure, _scopeKey);
        return this;
    }

    public IOutboxBuilder WithMetadata(Action<IServiceProvider, IOutboxMessageMetadataBuilder> configure)
    {
        _services.AddMessagesMetadata(configure);
        return this;
    }

    public IOutboxBuilder WithDeliveries(Action<IDeliveryBuilder> build)
    {
        _services.AddOutboxDelivery(build, _scopeKey);
        return this;
    }

    public IOutboxBuilder WithDeliveryConfigurationSection(
        string? sectionPrefix = OutboxDeliveryConfiguration.DefaultSectionPrefix)
    {
        // Replace the default convention registration: the last call wins, and null disables it.
        _services.RemoveAll<OutboxDeliveryConfiguration>();
        _services.AddSingleton(new OutboxDeliveryConfiguration(sectionPrefix));
        return this;
    }

    public IOutboxBuilder WithTenants(Action<IServiceProvider, TenantSettings> configure)
    {
        _services.AddTenantSettings(configure);
        return this;
    }

    public IOutboxBuilder WithDeliveryBatcher<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
            where TImplementation : class, IDeliveryBatcher
    {
        _services
            .RemoveAll<IDeliveryBatcher>()
            .AddSingleton<IDeliveryBatcher, TImplementation>();

        return this;
    }

    public IOutboxBuilder WithIdleWaitStrategy<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
            where TImplementation : class, IEmptyQueueWaitStrategy
    {
        _services
            .RemoveAll<IEmptyQueueWaitStrategy>()
            .AddSingleton<IEmptyQueueWaitStrategy, TImplementation>();

        return this;
    }

    public IOutboxBuilder WithIdleWaitStrategy(IEmptyQueueWaitStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        _services
            .RemoveAll<IEmptyQueueWaitStrategy>()
            .AddSingleton(strategy);

        return this;
    }

    public IOutboxBuilder WithMessageSerializer(
        Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory)
    {
        ArgumentNullException.ThrowIfNull(messageSerializerFactory);

        _services.RemoveAll<IOutboxMessageSerializer>();
        _services.TryAddSingleton<IOutboxMessageSerializer>(messageSerializerFactory);

        return this;
    }

    public IOutboxBuilder WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer
    {
        ArgumentNullException.ThrowIfNull(instance);

        _services.RemoveAll<IOutboxMessageSerializer>();
        _services.TryAddSingleton<IOutboxMessageSerializer>(instance);

        return this;
    }

    public IOutboxBuilder WithMessageSerializer<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
            where TService : class, IOutboxMessageSerializer
    {
        _services.RemoveAll<IOutboxMessageSerializer>();
        _services.TryAddSingleton<IOutboxMessageSerializer, TService>();

        return this;
    }
}
