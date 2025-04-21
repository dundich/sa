using Sa.Extensions;
using Sa.Outbox.Exceptions;

namespace Sa.Outbox.Delivery;

internal class DeliveryCourier(IScopedConsumer scopedConsumer) : IDeliveryCourier
{
    // Asynchronous method to deliver messages
    public async ValueTask<int> Deliver<TMessage>(
        IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages,
        int maxDeliveryAttempts,
        CancellationToken cancellationToken)
    {
        try
        {
            await scopedConsumer.MessageProcessingAsync(outboxMessages, cancellationToken);
        }
        catch (Exception ex) when (!ex.IsCritical()) // Handle non-critical exceptions
        {
            HandleError(ex, outboxMessages);
        }

        return PostHandle(outboxMessages, maxDeliveryAttempts);
    }




    // Method to handle errors during message delivery
    private static void HandleError<TMessage>(Exception error, IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages)
    {
        foreach (var item in outboxMessages)
        {
            if (item.DeliveryResult.Code == 0) // If no previous delivery errors
            {
                item.Error(error ?? new DeliveryException("Unknown delivery error."), postpone: GenTimeSpan.Next());
            }
        }
    }

    // Method to post-handle the delivery results of messages
    private static int PostHandle<TMessage>(IReadOnlyCollection<IOutboxContext<TMessage>> messages, int maxDeliveryAttempts)
    {
        int successfulDeliveries = 0; // Counter for successfully delivered messages

        foreach (IOutboxContext message in messages)
        {
            if (IsPermanentError(message, maxDeliveryAttempts))
            {
                MarkAsPermanentError(message);
            }
            else if (message.DeliveryResult.Code == 0) // If delivery was successful
            {
                message.Ok();
                successfulDeliveries++; // Increment the success counter
            }
        }

        return successfulDeliveries; // Return the count of successfully delivered messages
    }

    // Check if the message should be marked as a permanent error
    private static bool IsPermanentError(IOutboxContext message, int maxDeliveryAttempts)
    {
        return message.DeliveryResult.Code >= 400 &&
               message.DeliveryResult.Code < 500 &&
               message.DeliveryInfo.Attempt + 1 > maxDeliveryAttempts;
    }

    // Mark the message as a permanent error
    private static void MarkAsPermanentError(IOutboxContext message)
    {
        Exception exception = message.Exception ?? new DeliveryPermanentException("Maximum delivery attempts exceeded", statusCode: 501);
        message.PermanentError(exception, statusCode: 501);
    }


    // Static class to generate random time spans for retry delays
    static class GenTimeSpan
    {
        // Method to generate a random TimeSpan between 10 and 45 minutes
        public static TimeSpan Next()
        {
            return TimeSpan.FromSeconds(Random.Shared.Next(60 * 10, 60 * 45));
        }
    }
}