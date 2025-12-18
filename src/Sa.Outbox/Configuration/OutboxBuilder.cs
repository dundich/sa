using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.Partitional;
using Sa.Outbox.Publication;

namespace Sa.Outbox.Configuration;

internal sealed class OutboxBuilder : IOutboxBuilder
{
    private readonly IServiceCollection _services;

    public OutboxBuilder(IServiceCollection services)
    {
        _services = services;
        services.AddMessagePublisher();
        services.TryAddSingleton<OutboxPublishSettings>(this.PublishSettings);
    }

    public OutboxPublishSettings PublishSettings { get; } = new();

    public IOutboxBuilder WithDeliveries(Action<IDeliveryBuilder> build)
    {
        _services.AddOutboxDelivery(build);
        return this;
    }

    public IOutboxBuilder WithPartitioningSupport(Action<IServiceProvider, PartitionalSettings> configure)
    {
        _services.AddPartitioningSupport(configure);
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
