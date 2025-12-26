using System.Diagnostics.CodeAnalysis;
using Sa.Extensions;
using Sa.Outbox.Support;

namespace Sa.Outbox;

public static class DeliveryBuilderExtension
{
    public static IDeliveryBuilder AddDeliveryScoped<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        this IDeliveryBuilder builder, Action<IServiceProvider, ConsumerGroupSettings>? configure = null
    )
    where TConsumer : class, IConsumer<TMessage>
    where TMessage : IOutboxPayloadMessage
        => builder.AddDeliveryScoped<TConsumer, TMessage>(GetHashName<TConsumer>(), configure);

    public static IDeliveryBuilder AddDelivery<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        this IDeliveryBuilder builder, Action<IServiceProvider, ConsumerGroupSettings>? configure = null
    )
    where TConsumer : class, IConsumer<TMessage>
    where TMessage : IOutboxPayloadMessage
        => builder.AddDelivery<TConsumer, TMessage>(GetHashName<TConsumer>(), configure);


    static string GetHashName<TConsumer>() => $"cg_{(typeof(TConsumer).FullName ?? typeof(TConsumer).Name).GetMurmurHash3()}";
}
