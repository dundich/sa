using System.Runtime.CompilerServices;
using Sa.Extensions;
using Sa.Outbox.Exceptions;
using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Delivers a batch of messages with error handling and retry mechanisms
/// </summary>
internal sealed class DeliveryCourier(IDeliveryScoped processor) : IDeliveryCourier
{
    /// <summary>
    /// Asynchronous method to deliver messages
    /// </summary>
    public async ValueTask<int> Deliver<TMessage>(
        ConsumeSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> outboxMessages,
        CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        if (outboxMessages.Length == 0) return 0;

        try
        {
            await processor.ConsumeInScope(settings, filter, outboxMessages, cancellationToken);
        }
        catch (Exception ex) when (!ex.IsCritical()) // Handle non-critical exceptions
        {
            HandleError(ex, outboxMessages.Span);
        }

        return PostHandle(outboxMessages.Span, settings.MaxDeliveryAttempts);
    }


    // Method to handle errors during message delivery
    private static void HandleError<TMessage>(Exception error, ReadOnlySpan<IOutboxContextOperations<TMessage>> outboxMessages)
    {
        foreach (IOutboxContextOperations<TMessage> item in outboxMessages)
        {
            if (item.DeliveryResult.Code == DeliveryStatusCode.Pending)
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
                MarkAsMaximumAttemptsError(message);
            }
            else if (message.DeliveryResult.Code == DeliveryStatusCode.Pending) // If delivery was successful
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
        => message.DeliveryResult.Code >= DeliveryStatusCode.Warn
            && message.DeliveryResult.Code < DeliveryStatusCode.Error
            && message.DeliveryInfo.Attempt + 1 > maxDeliveryAttempts;


    private readonly static DeliveryPermanentException s_DeliveryPermanentException = new("Maximum delivery attempts exceeded", statusCode: 501);

    // Mark the message as a permanent error
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkAsMaximumAttemptsError<TMessage>(IOutboxContextOperations<TMessage> message)
    {
        Exception exception = message.Exception ?? s_DeliveryPermanentException;
        message.Error(exception, statusCode: DeliveryStatusCode.MaximumAttemptsError);
    }


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
