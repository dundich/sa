using System.Diagnostics;
using Sa.Outbox.Exceptions;


namespace Sa.Outbox.Delivery;


[DebuggerDisplay("#{PayloadId}")]
/// <summary>
/// OutboxMessage
/// </summary>
internal sealed class OutboxContext<TMessage>(OutboxDeliveryMessage<TMessage> delivery, TimeProvider timeProvider)
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


    public void Ok(string? message = null)
        => SetDeliveryStatus(DeliveryStatusCode.Ok, message);

    public void Created(string? message = null)
        => SetDeliveryStatus(DeliveryStatusCode.Created, message);

    public void Accepted(string? message = null)
        => SetDeliveryStatus(DeliveryStatusCode.Accepted, message);

    public void Ok203(string? message = null)
        => SetDeliveryStatus(DeliveryStatusCode.Ok203, message);

    public void NoContent(string? message = null)
        => SetDeliveryStatus(DeliveryStatusCode.NoContent, message);



    public void Aborted(string? message = null)
        => SetDeliveryStatus(DeliveryStatusCode.Aborted, message);


    public void MovedPermanently(string? message = null)
        => SetDeliveryStatus(DeliveryStatusCode.MovedPermanently, message);


    public void Warn(Exception exception, string? message = null, TimeSpan? postpone = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var deliveryException = exception as DeliveryException;

        SetDeliveryStatus(
            deliveryException?.StatusCode ?? DeliveryStatusCode.Warn,
            message ?? exception.Message,
            exception,
            postpone ?? deliveryException?.PostponeAt);
    }



    public void Error(Exception exception, string? message = null)
        => ErrorWithCode(DeliveryStatusCode.Error, exception, message);

    public void Error501(Exception exception, string? message = null)
        => ErrorWithCode(DeliveryStatusCode.Error501, exception, message);

    public void Error502(Exception exception, string? message = null)
        => ErrorWithCode(DeliveryStatusCode.Error502, exception, message);

    public void Error503(Exception exception, string? message = null)
        => ErrorWithCode(DeliveryStatusCode.Error503, exception, message);

    public void Error504(Exception exception, string? message = null)
        => ErrorWithCode(DeliveryStatusCode.Error504, exception, message);

    public void Error505(Exception exception, string? message = null)
        => ErrorWithCode(DeliveryStatusCode.Error505, exception, message);

    public void Error506(Exception exception, string? message = null)
        => ErrorWithCode(DeliveryStatusCode.Error506, exception, message);

    public void Error507(Exception exception, string? message = null)
        => ErrorWithCode(DeliveryStatusCode.Error507, exception, message);


    public void ErrorMaxAttempts()
    {
        SetDeliveryStatus(
            DeliveryPermanentException.StatusCode,
            Exception?.Message ?? DeliveryPermanentException.Message,
            Exception ?? DeliveryPermanentException,
            null);
    }

    private void SetDeliveryStatus(
        DeliveryStatusCode statusCode,
        string? message = null,
        Exception? exception = null,
        TimeSpan? postpone = null)
    {
        DeliveryResult = new DeliveryStatus(
            statusCode,
            message ?? string.Empty,
            GetUtcNow());

        Exception = exception;
        PostponeAt = postpone ?? TimeSpan.Zero;
    }


    private void ErrorWithCode(DeliveryStatusCode errorCode, Exception exception, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!errorCode.IsError())
            throw new ArgumentException($"Code {errorCode} is not an error status", nameof(errorCode));

        SetDeliveryStatus(
            errorCode,
            message ?? exception.Message,
            exception,
            null);
    }

    public DateTimeOffset GetUtcNow() => (timeProvider ?? TimeProvider.System).GetUtcNow();


    private readonly static DeliveryPermanentException DeliveryPermanentException
        = new("Maximum delivery attempts exceeded", statusCode: DeliveryStatusCode.MaximumAttemptsError);
}
