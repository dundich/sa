namespace Sa.Outbox;

public interface IConsumerGroupNamingStrategy
{
    string GetConsumerGroupName<TConsumer>();
}

