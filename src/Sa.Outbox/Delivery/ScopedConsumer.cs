using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.Delivery;

internal class ScopedConsumer(IServiceProvider serviceProvider) : IScopedConsumer
{
    // Method to process messages using a consumer in scope
    public async Task MessageProcessingAsync<TMessage>(
        IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = serviceProvider.CreateScope();
        IConsumer<TMessage> consumer = scope.ServiceProvider.GetRequiredService<IConsumer<TMessage>>();
        await consumer.Consume(outboxMessages, cancellationToken);
    }
}
