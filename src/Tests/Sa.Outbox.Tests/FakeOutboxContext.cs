namespace Sa.Outbox.Tests;

using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Sa.Outbox.Delivery;
using Sa.Outbox.Exceptions;

/// <summary>
/// Fake implementation of <see cref="IOutboxContextOperations{TMessage}"/> that accepts a payloadId parameter.
/// </summary>
public sealed class FakeOutboxContext<TMessage>(
    string payloadId,
    int attempt = 0,
    DeliveryStatusCode initialStatus = DeliveryStatusCode.Pending) : IOutboxContextOperations<TMessage>
{
    private DeliveryStatus _deliveryResult = new(initialStatus, payloadId, DateTimeOffset.UtcNow);
    private TimeSpan _postponeAt;
    private Exception? _exception;

    public Guid OutboxId { get; set; } = Guid.NewGuid();
    public string PayloadId { get; set; } = payloadId;
    public TMessage Payload { get; set; } = default!;
    public OutboxPartInfo PartInfo { get; set; } = new(1, "part-1", DateTimeOffset.UtcNow);

    private int _attempt = attempt;

    public OutboxTaskDeliveryInfo DeliveryInfo
    {
        get
        {
            return new OutboxTaskDeliveryInfo(1L, 0L, _attempt, 0L, _deliveryResult, PartInfo);
        }
    }

    public DeliveryStatus DeliveryResult => _deliveryResult;
    public Exception? Exception => _exception;
    public TimeSpan PostponeAt => _postponeAt;

    public FakeOutboxContext() : this("fake-msg", 0, DeliveryStatusCode.Pending) { }

    public void Ok(string? message = null)
        => SetStatus(DeliveryStatusCode.Ok, message);

    public void Created(string? message = null)
        => SetStatus(DeliveryStatusCode.Created, message);

    public void Accepted(string? message = null)
        => SetStatus(DeliveryStatusCode.Accepted, message);

    public void Ok203(string? message = null)
        => SetStatus(DeliveryStatusCode.Ok203, message);

    public void NoContent(string? message = null)
        => SetStatus(DeliveryStatusCode.NoContent, message);

    public void Aborted(string? message = null)
        => SetStatus(DeliveryStatusCode.Aborted, message);

    public void MovedPermanently(string? message = null)
        => SetStatus(DeliveryStatusCode.MovedPermanently, message);

    public void Postpone(TimeSpan postpone, string? message = null)
        => SetStatus(DeliveryStatusCode.Postpone, message, postpone: postpone);

    public void Retry(TimeSpan postpone, string? message = null)
        => SetStatus(DeliveryStatusCode.Retry, message, postpone: postpone);

    public void Warn(Exception exception, string? message = null, TimeSpan? postpone = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _exception = exception;
        _postponeAt = postpone ?? TimeSpan.Zero;
        _deliveryResult = new DeliveryStatus(DeliveryStatusCode.Warn, message ?? exception.Message, GetUtcNow());
    }

    public void Error(Exception exception, string? message = null)
        => SetError(DeliveryStatusCode.Error, exception, message);

    public void Error501(Exception exception, string? message = null)
        => SetError(DeliveryStatusCode.Error501, exception, message);

    public void Error502(Exception exception, string? message = null)
        => SetError(DeliveryStatusCode.Error502, exception, message);

    public void Error503(Exception exception, string? message = null)
        => SetError(DeliveryStatusCode.Error503, exception, message);

    public void Error504(Exception exception, string? message = null)
        => SetError(DeliveryStatusCode.Error504, exception, message);

    public void Error505(Exception exception, string? message = null)
        => SetError(DeliveryStatusCode.Error505, exception, message);

    public void Error506(Exception exception, string? message = null)
        => SetError(DeliveryStatusCode.Error506, exception, message);

    public void Error507(Exception exception, string? message = null)
        => SetError(DeliveryStatusCode.Error507, exception, message);

    public void ErrorMaxAttempts()
    {
        var permEx = new DeliveryPermanentException(
            _exception?.Message ?? "Maximum delivery attempts exceeded",
            statusCode: DeliveryStatusCode.MaximumAttemptsError);
        SetStatus(DeliveryStatusCode.MaximumAttemptsError, permEx.Message, exception: permEx);
    }

    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;

    private void SetStatus(DeliveryStatusCode code, string? message, TimeSpan? postpone = null, Exception? exception = null)
    {
        _deliveryResult = new DeliveryStatus(code, message ?? "", GetUtcNow());
        _postponeAt = postpone ?? TimeSpan.Zero;
        _exception = exception;
    }

    private void SetError(DeliveryStatusCode code, Exception exception, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        SetStatus(code, message ?? exception.Message, exception: exception);
    }
}

/// <summary>
/// Fake implementation of <see cref="IDeliveryLifetimeInvoker"/> for testing.
/// </summary>
public sealed class FakeDeliveryLifetimeInvoker(Func<CancellationToken, Task> consumeFn) : IDeliveryLifetimeInvoker
{
    public readonly List<CancellationToken> Invocations = [];

    public Task ConsumeInScope<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        Invocations.Add(cancellationToken);
        return consumeFn(cancellationToken);
    }
}

/// <summary>
/// Fake implementation of <see cref="IRetryStrategy"/> for testing.
/// </summary>
public sealed class FakeRetryStrategy(Func<int, TimeSpan> backoffFn) : IRetryStrategy
{
    public readonly List<int> RecordedAttempts = [];

    public TimeSpan GetBackoff(int attemptNumber)
    {
        RecordedAttempts.Add(attemptNumber);
        return backoffFn(attemptNumber);
    }
}
