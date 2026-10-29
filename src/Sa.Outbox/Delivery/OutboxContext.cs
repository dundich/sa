using Sa.Outbox.Exceptions;
using System.Diagnostics;


namespace Sa.Outbox.Delivery;


[DebuggerDisplay("#{PayloadId}")]
internal sealed class OutboxContext<TMessage> : IOutboxContextOperations<TMessage>
{
    private OutboxDeliveryMessage<TMessage> _delivery = default!;
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>Creates an empty context for the pool; <see cref="Reset"/> binds it to a message.</summary>
    public OutboxContext()
    {
    }

    public OutboxContext(OutboxDeliveryMessage<TMessage> delivery, TimeProvider? timeProvider = null)
        => Reset(delivery, timeProvider);

    /// <summary>
    /// Binds a pooled context to a delivery message and clears everything the previous delivery
    /// left behind.
    /// </summary>
    internal void Reset(OutboxDeliveryMessage<TMessage> delivery, TimeProvider? timeProvider)
    {
        _delivery = delivery;
        _timeProvider = timeProvider ?? TimeProvider.System;
        DeliveryResult = default;
        PostponeDelay = TimeSpan.Zero;
        Exception = null;
    }

    /// <summary>
    /// Drops the references a finished delivery held, so a pooled context does not keep the
    /// payload, delivery info or exception alive until it is rented again.
    /// </summary>
    internal void Clear()
    {
        _delivery = default!;
        _timeProvider = TimeProvider.System;
        DeliveryResult = default;
        PostponeDelay = TimeSpan.Zero;
        Exception = null;
    }

    public Guid OutboxId => _delivery.OutboxId;
    public string PayloadId => _delivery.Message.PayloadId;
    public TMessage Payload => _delivery.Message.Payload;
    public OutboxPartInfo PartInfo => _delivery.Message.PartInfo;
    public OutboxTaskDeliveryInfo DeliveryInfo => _delivery.DeliveryInfo;


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
