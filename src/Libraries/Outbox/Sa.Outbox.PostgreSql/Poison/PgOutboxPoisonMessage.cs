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
/// <param name="OutboxId">The message id (<c>msg_id</c>).</param>
/// <param name="TaskId">The task id (<c>task_id</c>), for correlation with <c>task$</c> and <c>__log$</c>.</param>
/// <param name="PayloadId">The caller-supplied business id (<c>msg_payload_id</c>).</param>
/// <param name="PayloadType">The full payload type name, for diagnostics.</param>
/// <param name="ConsumerGroup">The consumer group that gave up on the message.</param>
/// <param name="TenantId">The tenant the message belongs to.</param>
/// <param name="Part">The outbox part name the message was published under.</param>
/// <param name="Attempt">
/// The failed delivery number, 1-based and including this one: the stored
/// <c>delivery_attempt</c> plus one.
/// </param>
/// <param name="StatusCode">The permanent code: <c>Error</c>/<c>Error5xx</c> or <c>MaximumAttemptsError</c>.</param>
/// <param name="StatusMessage">The status text set by the consumer or the courier.</param>
/// <param name="Exception">The consumer exception.</param>
/// <param name="Payload">The payload serialized with the provider's <c>IOutboxMessageSerializer</c>.</param>
public sealed record PgOutboxPoisonMessage(
    Guid OutboxId,
    long TaskId,
    string PayloadId,
    string PayloadType,
    string ConsumerGroup,
    int TenantId,
    string Part,
    int Attempt,
    DeliveryStatusCode StatusCode,
    string StatusMessage,
    Exception Exception,
    ReadOnlyMemory<byte> Payload);
