using Sa.Extensions;
using Sa.Outbox.Support;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Delivery;

public partial interface IDeliveryBuilder
{
    private static IConsumerGroupNamingStrategy _defaultNamingStrategy = new DefaultConsumerGroupNamingStrategy();

    public IDeliveryBuilder AddDeliveryScoped<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        Action<IServiceProvider, ConsumerGroupSettings>? configure = null
    )
    where TConsumer : class, IConsumer<TMessage>
    where TMessage : IOutboxPayloadMessage
        => AddDeliveryScoped<TConsumer, TMessage>(GetConsumerGroupName<TConsumer>(), configure);

    public IDeliveryBuilder AddDelivery<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        Action<IServiceProvider, ConsumerGroupSettings>? configure = null
    )
    where TConsumer : class, IConsumer<TMessage>
    where TMessage : IOutboxPayloadMessage
        => AddDelivery<TConsumer, TMessage>(GetConsumerGroupName<TConsumer>(), configure);


    public static string GetConsumerGroupName<TConsumer>() => _defaultNamingStrategy.GetConsumerGroupName<TConsumer>();


    public static void ConfigureDefaultNamingStrategy(IConsumerGroupNamingStrategy strategy)
        => _defaultNamingStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));

    class DefaultConsumerGroupNamingStrategy : IConsumerGroupNamingStrategy
    {
        string IConsumerGroupNamingStrategy.GetConsumerGroupName<TConsumer>()
            => $"cg_{(typeof(TConsumer).FullName ?? typeof(TConsumer).Name).GetMurmurHash3()}";
    }
}
