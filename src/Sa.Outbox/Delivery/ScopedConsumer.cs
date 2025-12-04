using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages using a consumer in scope
/// </summary>
internal sealed class ScopedConsumer(IServiceProvider serviceProvider) : IScopedConsumer
{
    // Method to process messages using a consumer in scope
    public async Task MessageProcessingAsync<TMessage>(
        ConsumeSettings settings,
        IReadOnlyCollection<IOutboxContextOperations<TMessage>> outboxMessages,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        try
        {
            IConsumer<TMessage> consumer = scope.ServiceProvider.GetRequiredKeyedService<IConsumer<TMessage>>(settings);
            await consumer.Consume(settings, outboxMessages, cancellationToken);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }
}
