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
    /// Post-processes message statuses after consumption.
    ///
    /// Rules:
    ///   1. Messages left in a retryable warning state (Warn) that have exhausted
    ///      <c>MaxDeliveryAttempts</c> are marked as permanent errors (ErrorMaxAttempts) so they
    ///      stop being retried. This check must run BEFORE the "already handled" short-circuit,
    ///      otherwise a permanently failing message would be retried forever.
    ///   2. Messages already marked by the consumer (Ok, Created, Warn, Error, etc.) are left
    ///      untouched — their status reflects the consumer's decision.
    ///   3. Messages still in Pending state are auto-marked as Ok (the consumer didn't touch them,
    ///      meaning no exception was thrown → implicit success).
    ///
    /// Returns the number of messages handled, regardless of the status each one ended up in.
    /// </summary>
    private static int PostHandle<TMessage>(ReadOnlySpan<IOutboxContextOperations<TMessage>> messages, int maxDeliveryAttempts)
    {
        foreach (var message in messages)
        {
            // Retryable failure that ran out of attempts → dead letter. Checked first,
            // otherwise the short-circuit below would skip it and retry forever.
            if (IsAttemptsError(message, maxDeliveryAttempts))
            {
                message.ErrorMaxAttempts();
                continue;
            }

            // Consumer didn't touch this message — treat as success. Anything else was already
            // decided by the consumer and is left alone.
            if (message.DeliveryResult.Code.IsPending())
                message.Ok();
        }

        return messages.Length;
    }

    /// <summary>
    /// Check if the message should be marked as a permanent error: it is in a retryable
    /// warning state and has run out of delivery attempts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAttemptsError(IOutboxContext message, int maxDeliveryAttempts)
        => message.DeliveryResult.Code.IsWarning()
            && message.DeliveryInfo.Attempt + 1 > maxDeliveryAttempts;
}
