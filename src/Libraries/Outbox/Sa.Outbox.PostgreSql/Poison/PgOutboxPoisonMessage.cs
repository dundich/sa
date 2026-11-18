using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Poison;

/// <summary>
/// A message the PostgreSQL provider parked as a permanent failure: the consumer returned
/// <c>Error</c>/<c>Error5xx</c>, or the core courier exhausted the consumer-level
/// <c>MaxDeliveryAttempts</c> and set <c>MaximumAttemptsError</c>.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL keeps the task row and a de-duplicated <c>__error$</c> row, so the provider has a
/// built-in persistence strategy. <see cref="IPgOutboxPoisonHandler"/> intercepts the failure before
/// that write and can keep it (<see cref="PgOutboxPoisonAction.Default"/>) or take over
/// (<see cref="PgOutboxPoisonAction.Handled"/>).
/// </para>
/// <para>
/// If the handler throws, the batch is not finalized: the task stays <c>Processing</c>, its lock
/// expires and it is re-rented, so handling is at-least-once and the handler must tolerate
/// duplicates.
/// </para>
/// </remarks>
public sealed record PgOutboxPoisonMessage
{
    /// <summary>OutboxId.</summary>
    public Guid OutboxId { get; init; }
    /// <summary>TaskId.</summary>
    public long TaskId { get; init; }
    /// <summary>PayloadId.</summary>
    public required string PayloadId { get; init; }
    /// <summary>PayloadType.</summary>
    public required string PayloadType { get; init; }
    /// <summary>ConsumerGroup.</summary>
    public required string ConsumerGroup { get; init; }
    /// <summary>TenantId.</summary>
    public int TenantId { get; init; }
    /// <summary>Part.</summary>
    public required string Part { get; init; }
    /// <summary>Attempt.</summary>
    public int Attempt { get; init; }
    /// <summary>StatusCode.</summary>
    public DeliveryStatusCode StatusCode { get; init; }
    /// <summary>StatusMessage.</summary>
    public required string StatusMessage { get; init; }
    /// <summary>Exception.</summary>
    public required Exception Exception { get; init; }
    /// <summary>Payload.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }
}
