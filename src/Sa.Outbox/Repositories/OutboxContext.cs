using Sa.Outbox.Exceptions;


namespace Sa.Outbox.Repository;

/// <summary>
/// OutboxMessage
/// </summary>
internal sealed class OutboxContext<TMessage>(OutboxDeliveryMessage<TMessage> delivery, TimeProvider timeProvider)
    : IOutboxContextOperations<TMessage>
{
    public string OutboxId => delivery.OutboxId;
    public string PayloadId => delivery.Message.PayloadId;
    public TMessage Payload => delivery.Message.Payload;
    public OutboxPartInfo PartInfo => delivery.Message.PartInfo;
    public OutboxTaskDeliveryInfo DeliveryInfo => delivery.DeliveryInfo;


    public DeliveryStatus DeliveryResult { get; private set; }
    public TimeSpan PostponeAt { get; private set; }
    public Exception? Exception { get; private set; }

    public void PermanentError(Exception exception, string? message = null, int statusCode = DeliveryStatusCode.PermanentError)
        => Error(exception, message, statusCode);

    public void Error(Exception exception, string? message = null, int statusCode = DeliveryStatusCode.Error, TimeSpan? postpone = null)
    {
        DeliveryException? deliveryException = exception as DeliveryException;

        DeliveryResult = new DeliveryStatus(deliveryException?.StatusCode ?? statusCode, message ?? exception.Message, timeProvider.GetUtcNow());
        Exception = exception;
        PostponeAt = postpone ?? deliveryException?.PostponeAt ?? TimeSpan.Zero;
    }

    public void Ok(string? message = null)
    {
        DeliveryResult = new DeliveryStatus(DeliveryStatusCode.Ok, message ?? string.Empty, timeProvider.GetUtcNow());
        Exception = null;
        PostponeAt = TimeSpan.Zero;
    }

    public void Postpone(TimeSpan postpone, string? message = null)
    {
        DeliveryResult = new DeliveryStatus(DeliveryStatusCode.Postpone, message ?? string.Empty, timeProvider.GetUtcNow());
        Exception = null;
        PostponeAt = postpone;
    }

    public void Aborted(string? message = null)
    {
        DeliveryResult = new DeliveryStatus(DeliveryStatusCode.Aborted, message ?? string.Empty, timeProvider.GetUtcNow());
        Exception = null;
        PostponeAt = TimeSpan.Zero;
    }
}
