using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.Job;
using Sa.Outbox.Support;

namespace Sa.Outbox.Configuration;

internal sealed partial class DeliveryBuilder(IServiceCollection services) : IDeliveryBuilder
{
    public IDeliveryBuilder AddDelivery<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxDeliverySettings>? configure = null
    )
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : IOutboxPayloadMessage
    {
        services.AddDeliveryJob<TConsumer, TMessage>(SanitizeString(consumerGroupId), false, configure);
        return this;
    }

    public IDeliveryBuilder AddDeliverySingleton<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxDeliverySettings>? configure = null
    )
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : IOutboxPayloadMessage
    {
        services.AddDeliveryJob<TConsumer, TMessage>(SanitizeString(consumerGroupId), true, configure);
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

    static string SanitizeString(string input)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(input);
        return SanitazeRegex().Replace(input, "_").ToLower();
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_]")]
    private static partial Regex SanitazeRegex();
}
