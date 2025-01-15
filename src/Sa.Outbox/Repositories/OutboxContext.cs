using Sa.Outbox.Exceptions;
using Sa.Timing.Providers;


namespace Sa.Outbox.Repository;

/// <summary>
/// OutboxMessage
/// </summary>
internal class OutboxContext<TMessage>(OutboxDeliveryMessage<TMessage> delivery, ICurrentTimeProvider timeProvider) : IOutboxContext<TMessage>
{
    public string OutboxId { get; } = delivery.OutboxId;
    public OutboxPartInfo PartInfo { get; } = delivery.PartInfo;


    public string PayloadId { get; } = delivery.PayloadId;
    public TMessage Payload { get; } = delivery.Payload;


    public OutboxDeliveryInfo DeliveryInfo { get; } = delivery.DeliveryInfo;


    public DeliveryStatus DeliveryResult { get; private set; }
    public TimeSpan PostponeAt { get; private set; }
    public Exception? Exception { get; private set; }

    public IOutboxContext PermanentError(Exception exception, string? message = null, int statusCode = DeliveryStatusCode.PermanentError)
    {
        return Error(exception, message, statusCode);
    }

    public IOutboxContext Error(Exception exception, string? message = null, int statusCode = DeliveryStatusCode.Error, TimeSpan? postpone = null)
    {
        DeliveryException? deliveryException = exception as DeliveryException;

        DeliveryResult = new DeliveryStatus(deliveryException?.StatusCode ?? statusCode, message ?? exception.Message, timeProvider.GetUtcNow());
        Exception = exception;
        PostponeAt = postpone ?? deliveryException?.PostponeAt ?? TimeSpan.Zero;
        return this;
    }

    public IOutboxContext Ok(string? message = null)
    {
        DeliveryResult = new DeliveryStatus(DeliveryStatusCode.Ok, message ?? string.Empty, timeProvider.GetUtcNow());
        Exception = null;
        PostponeAt = TimeSpan.Zero;
        return this;
    }

    public IOutboxContext Postpone(TimeSpan postpone, string? message = null)
    {
        DeliveryResult = new DeliveryStatus(DeliveryStatusCode.Postpone, message ?? string.Empty, timeProvider.GetUtcNow());
        Exception = null;
        PostponeAt = postpone;
        return this;
    }

    public IOutboxContext Aborted(string? message = null)
    {
        DeliveryResult = new DeliveryStatus(DeliveryStatusCode.Aborted, message ?? string.Empty, timeProvider.GetUtcNow());
        Exception = null;
        PostponeAt = TimeSpan.Zero;
        return this;
    }
}
