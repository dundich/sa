using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// The state of one message inside one consumer group: the task row's current status, attempts and
/// error reference.
/// </summary>
/// <remarks>
/// The status is the last outcome the consumer reported (or <see cref="DeliveryStatusCode.Pending"/>
/// before the first delivery). <see cref="StatusMessage"/> is the free-text message written with that
/// outcome; when the consumer reported an exception it usually carries the exception message. The full
/// exception detail — type and composed text — is in <see cref="ErrorType"/>/<see cref="ErrorMessage"/>
/// when the provider logged it in <c>__error$</c>.
/// </remarks>
public sealed record PgOutboxDeliveryState
{
    /// <summary>The consumer group this state belongs to.</summary>
    public required string ConsumerGroup { get; init; }

    /// <summary>
    /// The task's surrogate id, or <see langword="null"/> when the group has not loaded the message.
    /// This is the id <see cref="Poison.PgOutboxPoisonMessage.TaskId"/> reports, so a handler can hand
    /// it straight to <see cref="IPgOutboxMessageMaintenance.RequeueTaskAsync"/>.
    /// </summary>
    public long? TaskId { get; init; }

    /// <summary>The last reported delivery status.</summary>
    public required DeliveryStatusCode StatusCode { get; init; }

    /// <summary>The free-text message written with <see cref="StatusCode"/>; empty when none.</summary>
    public required string StatusMessage { get; init; }

    /// <summary>The delivery attempt counter (<c>delivery_attempt</c>).</summary>
    /// <remarks>
    /// A <see cref="DeliveryStatusCode.Postpone"/> outcome deliberately does not increase it: the
    /// consumer governs deferral itself and decides when to give up.
    /// </remarks>
    public int Attempt { get; init; }

    /// <summary>When the group's loader created the task row.</summary>
    public required DateTimeOffset TaskCreatedAt { get; init; }

    /// <summary>When the last delivery finished, or <see langword="null"/> if none has finished yet.</summary>
    public DateTimeOffset? DeliveredAt { get; init; }

    /// <summary>
    /// When the current lease expires. A re-rentable task reports the instant it becomes visible to a
    /// worker again; <see langword="null"/> means no lease has been taken yet.
    /// </summary>
    public DateTimeOffset? LockExpiresOn { get; init; }

    /// <summary>The <c>__error$</c> id the task references, when the last outcome was an error.</summary>
    public long? ErrorId { get; init; }

    /// <summary>The exception type name stored in <c>__error$</c>, when there is one.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The composed exception text stored in <c>__error$</c>, when there is one.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether the task is stuck: it is <see cref="DeliveryStatusCode.Processing"/> but its lease has
    /// already expired, so the next poll re-rents it. This is the normal outcome of a crashed worker,
    /// not a failure. A processing task with no lease recorded at all also counts as expired — that
    /// is how the provider's own rent predicate reads a zero lease.
    /// </summary>
    public bool IsStuck { get; init; }

    /// <summary>
    /// Whether no further delivery is expected for this group without a manual requeue. Mirrors the
    /// set the provider's lock-and-select query skips: everything except
    /// <c>Pending</c>/<c>Processing</c>/<c>Postpone</c>/<c>Retry</c>/<c>Warn</c>.
    /// </summary>
    public bool IsTerminal => StatusCode is not (
        DeliveryStatusCode.Pending
        or DeliveryStatusCode.Processing
        or DeliveryStatusCode.Postpone
        or DeliveryStatusCode.Retry
        or DeliveryStatusCode.Warn);
}
