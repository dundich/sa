using Sa.Outbox.Exceptions;
using System.Diagnostics;


namespace Sa.Outbox.Delivery;


[DebuggerDisplay("#{PayloadId}")]
internal sealed class OutboxContext<TMessage>(
    OutboxDeliveryMessage<TMessage> delivery,
    TimeProvider? timeProvider = null)
    : IOutboxContextOperations<TMessage>
{

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Guid OutboxId => delivery.OutboxId;
    public string PayloadId => delivery.Message.PayloadId;
    public TMessage Payload => delivery.Message.Payload;
    public OutboxPartInfo PartInfo => delivery.Message.PartInfo;
    public OutboxTaskDeliveryInfo DeliveryInfo => delivery.DeliveryInfo;


    public DeliveryStatus DeliveryResult { get; private set; }
    public TimeSpan PostponeDelay { get; private set; } = TimeSpan.Zero;
    public Exception? Exception { get; private set; }

    /// <summary>
    /// Defers the message without consuming a delivery attempt — the attempt counter is incremented
    /// for every other status, but never for <see cref="DeliveryStatusCode.Postpone"/>.
    /// </summary>
    /// <remarks>
    /// This is intentional: postponement is governed by the consumer, which decides when and whether
    /// to give up. Consequently <c>MaxDeliveryAttempts</c> does not apply here — a consumer that
    /// postpones forever keeps the message alive forever, and the dead-lettering decision is its own
    /// responsibility (it must eventually call <c>Error</c>/<c>Error5xx</c> or start failing).
    /// The delay is applied as the lease extension (<c>lock_expires_on = created_at + delay</c>), so
    /// the task cannot be re-rented before the delay elapses.
    /// </remarks>
    public void Postpone(TimeSpan postponeDelay, string? message = null)
        => SetDeliveryStatus(DeliveryStatusCode.Postpone, message, null, postponeDelay);

    public void Retry(TimeSpan postponeDelay, string? message = null)
        => SetDeliveryStatus(DeliveryStatusCode.Retry, message, null, postponeDelay);

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


    public void Warn(Exception exception, string? message = null, TimeSpan? postponeDelay = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var deliveryException = exception as DeliveryException;

        SetDeliveryStatus(
            deliveryException?.StatusCode ?? DeliveryStatusCode.Warn,
            message ?? exception.Message,
            exception,
            postponeDelay ?? deliveryException?.PostponeDelay);
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
        TimeSpan? postponeDelay = null)
    {
        DeliveryResult = new DeliveryStatus(
            statusCode,
            message ?? string.Empty,
            GetUtcNow());

        Exception = exception;
        PostponeDelay = postponeDelay ?? TimeSpan.Zero;
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

    public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();


    private readonly static DeliveryPermanentException DeliveryPermanentException
        = new("Maximum delivery attempts exceeded", statusCode: DeliveryStatusCode.MaximumAttemptsError);
}
