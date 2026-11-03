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
    private readonly ConcurrentDictionary<(Type MessageType, string ProviderKey, string ConsumerGroupId), IConsumer> _singletonConsumers = new();

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
        var consumer = GetOrCreateSingletonConsumer<TMessage>(settings);
        return ProcessMessages(consumer, settings, filter, messages, cancellationToken);
    }

    private async Task ProcessInNewScope<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        IConsumer<TMessage> consumer = GetConsumer<TMessage>(scope.ServiceProvider, settings);
        await ProcessMessages(consumer, settings, filter, messages, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the consumer by the provider + group key. Falls back to the plain group id, so a
    /// consumer registered manually under the legacy string key (or before multi-provider existed)
    /// keeps resolving.
    /// </summary>
    private static IConsumer<TMessage> GetConsumer<TMessage>(IServiceProvider sp, OutboxConsumerSettings settings)
    {
        var key = new OutboxGroupKey(settings.ProviderKey, settings.ConsumerGroupId);

        return sp.GetKeyedService<IConsumer<TMessage>>(key)
            ?? sp.GetRequiredKeyedService<IConsumer<TMessage>>(settings.ConsumerGroupId);
    }

    private static async Task ProcessMessages<TMessage>(
        IConsumer<TMessage> consumer,
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        await consumer.Consume(settings, filter, messages, cancellationToken).ConfigureAwait(false);
    }

    private IConsumer<TMessage> GetOrCreateSingletonConsumer<TMessage>(OutboxConsumerSettings settings)
    {
        var key = (typeof(TMessage), settings.ProviderKey, settings.ConsumerGroupId);

        return (IConsumer<TMessage>)_singletonConsumers.GetOrAdd(
            key,
            _ => GetConsumer<TMessage>(serviceProvider, settings));
    }
}
