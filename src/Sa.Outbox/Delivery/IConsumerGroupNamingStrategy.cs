namespace Sa.Outbox.Delivery;

public interface IConsumerGroupNamingStrategy
{
    string GetConsumerGroupName<TConsumer>();
}

