using Sa.Extensions;
using System.Runtime.CompilerServices;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Delivers a batch of messages with error handling and retry mechanisms
/// </summary>
internal sealed class DeliveryCourier(
    IDeliveryLifetimeInvoker processor,
     IRetryStrategy? retryStrategy = null) : IDeliveryCourier
{

    private readonly IRetryStrategy _retryStrategy = retryStrategy ?? ExponentialBackoffRetryStrategy.Shared;

    /// <summary>
    /// Asynchronous method to deliver messages
    /// </summary>
    public async ValueTask<int> Deliver<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        if (messages.IsEmpty) return 0;

        try
        {
            await processor.ConsumeInScope(settings, filter, messages, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ex.IsCritical()) // Handle non-critical exceptions
        {
            HandleError(ex, messages.Span);
        }

        return PostHandle(messages.Span, settings.MaxDeliveryAttempts);
    }


    // Method to handle errors during message delivery
    private void HandleError<TMessage>(Exception error, ReadOnlySpan<IOutboxContextOperations<TMessage>> messages)
    {
        foreach (IOutboxContextOperations<TMessage> message in messages)
        {
            if (message.DeliveryResult.Code.IsPending())
            {
                var attempt = message.DeliveryInfo.Attempt + 1;
                var backoff = _retryStrategy.GetBackoff(attempt);

                message.Warn(
                    error,
                    postpone: backoff);
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
            else if (message.DeliveryResult.Code.IsPending()) // If delivery was successful
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
        => message.DeliveryResult.Code.IsWarning()
            && message.DeliveryInfo.Attempt + 1 > maxDeliveryAttempts;
}
