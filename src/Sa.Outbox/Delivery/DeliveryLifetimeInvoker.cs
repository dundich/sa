using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages using a consumer in scope
/// </summary>
internal sealed class DeliveryLifetimeInvoker(IServiceProvider serviceProvider) : IDeliveryLifetimeInvoker
{

    private readonly ConcurrentDictionary<Guid, IConsumer> _singletonConsumers = new();

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
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        IConsumer<TMessage> consumer = GetConsumer<TMessage>(scope.ServiceProvider, settings.Id);
        await ProcessMessages(consumer, settings, filter, messages, cancellationToken);
    }

    private static IConsumer<TMessage> GetConsumer<TMessage>(IServiceProvider sp, Guid id)
        => sp.GetRequiredKeyedService<IConsumer<TMessage>>(id);

    private static async Task ProcessMessages<TMessage>(
        IConsumer<TMessage> consumer,
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        await consumer.Consume(settings, filter, messages, cancellationToken);
    }

    private IConsumer<TMessage> GetOrCreateSingletonConsumer<TMessage>(
        OutboxConsumerSettings settings)
    {
        return (IConsumer<TMessage>)_singletonConsumers.GetOrAdd(settings.Id, key =>
            GetConsumer<TMessage>(serviceProvider, key));
    }
}
