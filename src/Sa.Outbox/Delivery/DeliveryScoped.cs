using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages using a consumer in scope
/// </summary>
internal sealed class DeliveryScoped(IServiceProvider serviceProvider) : IDeliveryScoped
{
    // Method to process messages using a consumer in scope
    public async Task ConsumeInScope<TMessage>(
        ConsumeSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> outboxMessages,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        try
        {
            IConsumer<TMessage> consumer = scope.ServiceProvider.GetRequiredKeyedService<IConsumer<TMessage>>(settings);
            await consumer.Consume(settings, filter, outboxMessages, cancellationToken);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }
}
