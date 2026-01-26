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

    private OutboxBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public static OutboxBuilder Create(IServiceCollection services)
    {
        services.AddMessagePublisher();
        return new OutboxBuilder(services);
    }

    public IOutboxBuilder WithPublishSettings(Action<IServiceProvider, OutboxPublishSettings> configure)
    {
        _services.AddMessagePublisher(configure);
        return this;
    }

    public IOutboxBuilder WithMetadata(Action<IServiceProvider, IOutboxMessageMetadataBuilder> configure)
    {
        _services.AddOutboxMessages(configure);
        return this;
    }

    public IOutboxBuilder WithDeliveries(Action<IDeliveryBuilder> build)
    {
        _services.AddOutboxDelivery(build);
        return this;
    }

    public IOutboxBuilder WithTenants(Action<IServiceProvider, TenantSettings> configure)
    {
        _services.AddTenantProvider(configure);
        return this;
    }

    public IOutboxBuilder WithDeliveryBatcher<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
         where TImplementation : class, IDeliveryBatcher
    {
        _services
            .RemoveAll<IDeliveryBatcher>()
            .AddSingleton<IDeliveryBatcher, TImplementation>();

        return this;
    }
}
