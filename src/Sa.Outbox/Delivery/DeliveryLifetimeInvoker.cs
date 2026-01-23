using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Support;
using System.Collections.Concurrent;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages using a consumer in scope
/// </summary>
internal sealed class DeliveryLifetimeInvoker(IServiceProvider serviceProvider) : IDeliveryLifetimeInvoker
{

    private readonly ConcurrentDictionary<ConsumerGroupSettings, IConsumer> _singletonConsumers = new();

    // Method to process messages using a consumer in scope
    public Task ConsumeInScope<TMessage>(
        ConsumerGroupSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        return settings.AsSingleton
            ? ProcessInSingleton(settings, filter, messages, cancellationToken)
            : ProcessInNewScope(settings, filter, messages, cancellationToken);
    }

    private Task ProcessInSingleton<TMessage>(
        ConsumerGroupSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        var consumer = GetOrCreateSingletonConsumer<TMessage>(settings);
        return ProcessMessages(consumer, settings, filter, messages, cancellationToken);
    }

    private async Task ProcessInNewScope<TMessage>(
        ConsumerGroupSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        IConsumer<TMessage> consumer = scope.ServiceProvider.GetRequiredKeyedService<IConsumer<TMessage>>(settings);
        await ProcessMessages(consumer, settings, filter, messages, cancellationToken);
    }

    private static async Task ProcessMessages<TMessage>(
        IConsumer<TMessage> consumer,
        ConsumerGroupSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        await consumer.Consume(settings, filter, messages, cancellationToken);
    }

    private IConsumer<TMessage> GetOrCreateSingletonConsumer<TMessage>(
        ConsumerGroupSettings settings) where TMessage : IOutboxPayloadMessage
    {
        return (IConsumer<TMessage>)_singletonConsumers.GetOrAdd(settings, key =>
            serviceProvider.GetRequiredKeyedService<IConsumer<TMessage>>(key));
    }
}
