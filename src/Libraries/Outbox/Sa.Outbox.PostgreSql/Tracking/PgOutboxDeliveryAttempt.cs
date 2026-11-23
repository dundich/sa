using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// One finished delivery attempt, as recorded in the append-only <c>__log$</c> table.
/// </summary>
/// <remarks>
/// The log is written when a message's delivery finishes and is never updated, so a sequence of
/// attempts is the closest thing to a timeline the provider keeps. Delivery is at-least-once: a lease
/// that expires while the consumer is still working produces a second attempt, so the same message can
/// appear more than once with the same status.
/// </remarks>
public sealed record PgOutboxDeliveryAttempt
{
    /// <summary>The delivery record id (<c>delivery_id</c>), unique and increasing.</summary>
    public required long DeliveryId { get; init; }

    /// <summary>The consumer group that reported the outcome.</summary>
    public required string ConsumerGroup { get; init; }

    /// <summary>The task the attempt belongs to.</summary>
    public required long TaskId { get; init; }

    /// <summary>The status the consumer reported.</summary>
    public required DeliveryStatusCode StatusCode { get; init; }

    /// <summary>The free-text message written with the status; empty when none.</summary>
    public required string StatusMessage { get; init; }

    /// <summary>When the attempt finished.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The <c>__error$</c> id for an error outcome, when one was written.</summary>
    public long? ErrorId { get; init; }

    /// <summary>The exception type name stored in <c>__error$</c>, when there is one.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The composed exception text stored in <c>__error$</c>, when there is one.</summary>
    public string? ErrorMessage { get; init; }
}
