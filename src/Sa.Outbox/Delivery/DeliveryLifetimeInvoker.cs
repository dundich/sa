using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages using a consumer in scope
/// </summary>
internal sealed class DeliveryLifetimeInvoker(IServiceProvider serviceProvider) : IDeliveryLifetimeInvoker
{

    // Keyed by (message type, consumer group) rather than by group alone: a group id is only unique
    // per message type, so keying by group alone would hand a second TMessage the first one's
    // consumer instance and fail the cast below with InvalidCastException inside the delivery path.
    private readonly ConcurrentDictionary<(Type MessageType, string ConsumerGroupId), IConsumer> _singletonConsumers = new();

    // Method to process messages using a consumer in scope
    public Task ConsumeInScope<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        return settings.AsSingleton
            ? ProcessInSingleton(settings, filter, messages, cancellationToken)
            : ProcessInNewScope(settings, filter, messages, cancellationToken);
    }

    private Task ProcessInSingleton<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        var consumer = GetOrCreateSingletonConsumer<TMessage>(settings.ConsumerGroupId);
        return ProcessMessages(consumer, settings, filter, messages, cancellationToken);
    }

    private async Task ProcessInNewScope<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        IConsumer<TMessage> consumer = GetConsumer<TMessage>(scope.ServiceProvider, settings.ConsumerGroupId);
        await ProcessMessages(consumer, settings, filter, messages, cancellationToken).ConfigureAwait(false);
    }

    private static IConsumer<TMessage> GetConsumer<TMessage>(IServiceProvider sp, string key)
        => sp.GetRequiredKeyedService<IConsumer<TMessage>>(key);

    private static async Task ProcessMessages<TMessage>(
        IConsumer<TMessage> consumer,
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        await consumer.Consume(settings, filter, messages, cancellationToken).ConfigureAwait(false);
    }

    private IConsumer<TMessage> GetOrCreateSingletonConsumer<TMessage>(string consumerGroupId)
    {
        var key = (typeof(TMessage), consumerGroupId);

        return (IConsumer<TMessage>)_singletonConsumers.GetOrAdd(
            key,
            k => GetConsumer<TMessage>(serviceProvider, k.ConsumerGroupId));
    }
}
