using System.Runtime.CompilerServices;
using Sa.Extensions;
using Sa.Outbox.Exceptions;
using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Delivers a batch of messages with error handling and retry mechanisms
/// </summary>
internal sealed class DeliveryCourier(IDeliveryLifetimeInvoker processor) : IDeliveryCourier
{
    /// <summary>
    /// Asynchronous method to deliver messages
    /// </summary>
    public async ValueTask<int> Deliver<TMessage>(
        ConsumerGroupSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        if (messages.IsEmpty) return 0;

        try
        {
            await processor.ConsumeInScope(settings, filter, messages, cancellationToken);
        }
        catch (Exception ex) when (!ex.IsCritical()) // Handle non-critical exceptions
        {
            HandleError(ex, messages.Span);
        }

        return PostHandle(messages.Span, settings.ConsumeSettings.MaxDeliveryAttempts);
    }


    // Method to handle errors during message delivery
    private static void HandleError<TMessage>(Exception error, ReadOnlySpan<IOutboxContextOperations<TMessage>> messages)
    {
        foreach (IOutboxContextOperations<TMessage> item in messages)
        {
            if (DeliveryStatusCode.IsPending(item.DeliveryResult.Code))
            {
                item.Warn(
                    error ?? new DeliveryException("Unknown delivery error."),
                    postpone: RetryStrategy.CalculateBackoff());
            }
        }
    }


    /// <summary>
    /// Method to post-handle the delivery results of messages
    /// </summary>
    private static int PostHandle<TMessage>(ReadOnlySpan<IOutboxContextOperations<TMessage>> messages, int maxDeliveryAttempts)
    {
        int successfulDeliveries = 0; // Counter for successfully delivered messages

        foreach (var message in messages)
        {
            if (IsAttemptsError(message, maxDeliveryAttempts))
            {
                // Mark the message as a permanent error
                message.ErrorMaxAttempts();
            }
            else if (DeliveryStatusCode.IsPending(message.DeliveryResult.Code)) // If delivery was successful
            {
                message.Ok();
                successfulDeliveries++; // Increment the success counter
            }
        }

        return successfulDeliveries; // Return the count of successfully delivered messages
    }

    /// <summary>
    /// Check if the message should be marked as a permanent error
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAttemptsError(IOutboxContext message, int maxDeliveryAttempts)
        => DeliveryStatusCode.IsWarning(message.DeliveryResult.Code)
            && message.DeliveryInfo.Attempt + 1 > maxDeliveryAttempts;


    /// <summary>
    /// todos: customization
    /// Static class to generate random time spans for retry delays
    /// </summary>
    static class RetryStrategy
    {
        /// <summary>
        /// Method to generate a random TimeSpan between 10 and 45 minutes
        /// </summary>
        public static TimeSpan CalculateBackoff()
            => TimeSpan.FromSeconds(Random.Shared.Next(60 * 10, 60 * 45));
    }
}
