using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.Metadata;
using Sa.Outbox.Partitional;
using Sa.Outbox.Publication;
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
        services
            .AddMessagePublisher()
            .AddOutboxDelivery(scopeKey: scopeKey);

        return new OutboxBuilder(services, scopeKey);
    }

    public IOutboxBuilder WithPublishSettings(Action<IServiceProvider, OutboxPublishSettings> configure)
    {
        _services.AddMessagePublisher(configure);
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
}
