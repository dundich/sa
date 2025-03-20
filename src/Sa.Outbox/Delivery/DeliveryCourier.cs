using Microsoft.Extensions.DependencyInjection;
using Sa.Extensions;
using Sa.Outbox.Exceptions;

namespace Sa.Outbox.Delivery;

internal class DeliveryCourier(IServiceProvider serviceProvider) : IDeliveryCourier
{

    public async ValueTask<int> Deliver<TMessage>(IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages, int maxDeliveryAttempts, CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = serviceProvider.CreateScope();
            IConsumer<TMessage> consumer = scope.ServiceProvider.GetRequiredService<IConsumer<TMessage>>();
            await consumer.Consume(outboxMessages, cancellationToken);
        }
        catch (Exception ex) when (!ex.IsCritical())
        {
            HandleError(ex, outboxMessages);
        }

        return PostHandle(outboxMessages, maxDeliveryAttempts);
    }

    private static void HandleError<TMessage>(Exception error, IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages)
    {
        foreach (IOutboxContext<TMessage> item in outboxMessages)
        {
            // если не было обработанных ошибок до - то...
            if (item.DeliveryResult.Code == 0)
            {
                // "Unknown delivery error."
                // раскидываем ошибки в отложенную обработку от 10 до 45 мин
                item.Error(error ?? new DeliveryException("Unknown delivery error."), postpone: GenTimeSpan.Next());
            }
        }
    }

    private static int PostHandle<TMessage>(IReadOnlyCollection<IOutboxContext<TMessage>> messages, int maxDeliveryAttempts)
    {
        int iOk = 0;
        foreach (IOutboxContext<TMessage> message in messages)
        {
            if (message.DeliveryResult.Code >= 400 && message.DeliveryResult.Code < 500 && message.DeliveryInfo.Attempt + 1 > maxDeliveryAttempts)
            {
                Exception exception = message.Exception ?? new DeliveryPermanentException("Maximum delivery attempts exceeded", statusCode: 501);

                // Устанавливаем постоянную ошибку
                message.PermanentError(exception, statusCode: 501);
            }
            else if (message.DeliveryResult.Code == 0)
            {
                message.Ok();
                iOk++;
            }
        }

        return iOk;
    }


    static class GenTimeSpan
    {
        public static TimeSpan Next()
        {
            return TimeSpan.FromSeconds(Random.Shared.Next(60 * 10, 60 * 45));
        }
    }
}