using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

internal sealed class ScopedConsumer(IServiceProvider serviceProvider) : IScopedConsumer
{
    // Method to process messages using a consumer in scope
    public async Task MessageProcessingAsync<TMessage>(
        IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages,
        CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        try
        {
            IConsumer<TMessage> consumer = scope.ServiceProvider.GetRequiredService<IConsumer<TMessage>>();
            await consumer.Consume(outboxMessages, cancellationToken);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }
}
