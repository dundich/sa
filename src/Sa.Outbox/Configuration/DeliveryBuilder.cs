using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.Job;
using Sa.Outbox.Support;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Configuration;

internal sealed class DeliveryBuilder(IServiceCollection services) : IDeliveryBuilder
{
    public IDeliveryBuilder AddDelivery<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
            Action<IServiceProvider, OutboxDeliverySettings>? configure = null,
            int instanceCount = 1)
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : IOutboxPayloadMessage
    {
        services.AddDeliveryJob<TConsumer, TMessage>(configure, instanceCount);
        return this;
    }

    public IDeliveryBuilder AddDeliveryBatching<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
        where TImplementation : class, IDeliveryBatcher
    {
        services
            .RemoveAll<IDeliveryBatcher>()
            .TryAddSingleton<IDeliveryBatcher, TImplementation>();
        return this;
    }
}
