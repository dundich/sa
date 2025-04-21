using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Job;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Configuration;

internal class DeliveryBuilder(IServiceCollection services) : IDeliveryBuilder
{
    public IDeliveryBuilder AddDelivery<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(Action<IServiceProvider, OutboxDeliverySettings>? configure = null, int instanceCount = 1)
        where TConsumer : class, IConsumer<TMessage>
    {
        services.AddDeliveryJob<TConsumer, TMessage>(configure, instanceCount);
        return this;
    }
}
