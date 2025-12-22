using System.Diagnostics;
using Sa.Outbox.Exceptions;


namespace Sa.Outbox.Repository;


[DebuggerDisplay("#{PayloadId}")]
/// <summary>
/// OutboxMessage
/// </summary>
public sealed class OutboxContext<TMessage>(OutboxDeliveryMessage<TMessage> delivery, TimeProvider? timeProvider = null)
    : IOutboxContextOperations<TMessage>
{
    public Guid OutboxId => delivery.OutboxId;
    public string PayloadId => delivery.Message.PayloadId;
    public TMessage Payload => delivery.Message.Payload;
    public OutboxPartInfo PartInfo => delivery.Message.PartInfo;
    public OutboxTaskDeliveryInfo DeliveryInfo => delivery.DeliveryInfo;


    public DeliveryStatus DeliveryResult { get; private set; }
    public TimeSpan PostponeAt { get; private set; } = TimeSpan.Zero;
    public Exception? Exception { get; private set; }

    public void Error(Exception exception, string? message = null, int statusCode = DeliveryStatusCode.Error)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!DeliveryStatusCode.IsError(statusCode))
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        SetDeliveryStatus(
            statusCode,
            message ?? exception.Message,
            exception,
            null);
    }

    public void ErrorMaxAttempts()
    {
        SetDeliveryStatus(
            DeliveryPermanentException.StatusCode,
            Exception?.Message ?? DeliveryPermanentException.Message,
            Exception ?? DeliveryPermanentException,
            null);
    }

    public void Warn(Exception exception, string? message = null, TimeSpan? postpone = null, int statusCode = DeliveryStatusCode.Warn)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!DeliveryStatusCode.IsWarning(statusCode))
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }


        var deliveryException = exception as DeliveryException;

        SetDeliveryStatus(
            deliveryException?.StatusCode ?? statusCode,
            message ?? exception.Message,
            exception,
            postpone ?? deliveryException?.PostponeAt);
    }

    public void Ok(string? message = null, int statusCode = DeliveryStatusCode.Ok)
    {
        if (!DeliveryStatusCode.IsSuccess(statusCode))
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        SetDeliveryStatus(
            DeliveryStatusCode.Ok,
            message ?? string.Empty,
            null,
            TimeSpan.Zero);
    }

    public void Postpone(TimeSpan postpone, string? message = null)
    {
        if (postpone <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(postpone), "Postpone must be positive");

        SetDeliveryStatus(
            DeliveryStatusCode.Postpone,
            message ?? string.Empty,
            null,
            postpone);
    }

    public void Aborted(string? message = null)
    {
        SetDeliveryStatus(
            DeliveryStatusCode.Aborted,
            message ?? string.Empty,
            null,
            TimeSpan.Zero);
    }

    private void SetDeliveryStatus(
        int statusCode,
        string message,
        Exception? exception = null,
        TimeSpan? postpone = null)
    {
        DeliveryResult = new DeliveryStatus(
            statusCode,
            message,
            GetUtcNow());

        Exception = exception;
        PostponeAt = postpone ?? TimeSpan.Zero;
    }

    public DateTimeOffset GetUtcNow() => (timeProvider ?? TimeProvider.System).GetUtcNow();


    private readonly static DeliveryPermanentException DeliveryPermanentException
        = new("Maximum delivery attempts exceeded", statusCode: DeliveryStatusCode.MaximumAttemptsError);
}
