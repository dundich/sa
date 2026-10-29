using System.Collections.Concurrent;

namespace Sa.Outbox.Delivery;

internal sealed class OutboxContextFactory(TimeProvider? timeProvider = null) : IOutboxContextFactory
{
    /// <summary>Upper bound per message type; a burst may create more, but only this many are kept.</summary>
    private const int MaxPooledPerType = 256;

    public IOutboxContextOperations<TMessage> Create<TMessage>(OutboxDeliveryMessage<TMessage> deliveryMessage)
    {
        OutboxContext<TMessage> context = ContextPool<TMessage>.Rent();
        context.Reset(deliveryMessage, timeProvider ?? TimeProvider.System);
        return context;
    }

    public void Return<TMessage>(IOutboxContextOperations<TMessage> context)
    {
        // A custom factory may return its own context type; only ours can be pooled.
        if (context is OutboxContext<TMessage> owned)
        {
            ContextPool<TMessage>.Return(owned);
        }
    }

    /// <summary>
    /// Per-closed-generic pool. A context is thread-bound only while the delivery cycle runs; after
    /// <see cref="Return"/> it is cleared and handed to whoever rents it next, so this pool needs no
    /// per-tenant bookkeeping.
    /// </summary>
    private static class ContextPool<TMessage>
    {
        private static readonly ConcurrentBag<OutboxContext<TMessage>> Items = new();
        private static int _count;

        public static OutboxContext<TMessage> Rent()
        {
            if (Items.TryTake(out OutboxContext<TMessage>? context))
            {
                Interlocked.Decrement(ref _count);
                return context;
            }

            return new OutboxContext<TMessage>();
        }

        public static void Return(OutboxContext<TMessage> context)
        {
            // Drop the payload/delivery/exception references before the context can sit in the
            // pool between batches.
            context.Clear();

            if (Interlocked.Increment(ref _count) <= MaxPooledPerType)
            {
                Items.Add(context);
            }
            else
            {
                Interlocked.Decrement(ref _count);
            }
        }
    }
}
