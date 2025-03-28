using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.Partitional;
using Sa.Outbox.Publication;

namespace Sa.Outbox.Configuration;

internal class OutboxBuilder : IOutboxBuilder
{
    private readonly IServiceCollection services;

    public OutboxBuilder(IServiceCollection services)
    {
        this.services = services;
        services.AddSaInfrastructure();
        services.AddMessagePublisher();
        services.TryAddSingleton<OutboxPublishSettings>(this.PublishSettings);
    }

    public OutboxPublishSettings PublishSettings { get; } = new();

    public IOutboxBuilder WithDeliveries(Action<IDeliveryBuilder> build)
    {
        services.AddOutboxDelivery(build);
        return this;
    }

    public IOutboxBuilder WithPartitioningSupport(Action<IServiceProvider, PartitionalSettings> configure)
    {
        services.AddPartitioningSupport(configure);
        return this;
    }
}
