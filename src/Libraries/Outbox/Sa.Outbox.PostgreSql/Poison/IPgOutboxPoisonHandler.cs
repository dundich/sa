namespace Sa.Outbox.PostgreSql.Poison;

/// <summary>
/// Intercepts a permanent failure of the PostgreSQL Outbox <b>before</b> it is persisted: a consumer
/// <c>Error</c>/<c>Error5xx</c>, or the core courier's <c>MaximumAttemptsError</c>.
/// </summary>
/// <remarks>
/// <para>
/// The handler decides the strategy by its return value:
/// </para>
/// <list type="bullet">
/// <item><see cref="PgOutboxPoisonAction.Default"/> — continue with the built-in behaviour: the
/// de-duplicated <c>__error$</c> row is inserted and the task is finalized with its <c>error_id</c>;</item>
/// <item><see cref="PgOutboxPoisonAction.Handled"/> — the handler took responsibility: <c>__error$</c>
/// is skipped, but the task is still finalized as <c>Error</c>/<c>MaximumAttemptsError</c> with
/// <c>error_id = NULL</c>.</item>
/// </list>
/// <para>
/// Throwing is a third outcome: the batch is not finalized at all, the task stays <c>Processing</c>,
/// its lock expires and it is re-rented, so the handler runs again. Handling is therefore
/// at-least-once and the handler must tolerate duplicates.
/// </para>
/// </remarks>
public interface IPgOutboxPoisonHandler
{
    /// <summary>Handles one permanent failure and selects the persistence strategy.</summary>
    /// <param name="message">The message and the reason it was parked.</param>
    /// <param name="cancellationToken">The cancellation token of the delivery cycle.</param>
    /// <returns>What the provider should do after the handler returns.</returns>
    ValueTask<PgOutboxPoisonAction> Handle(PgOutboxPoisonMessage message, CancellationToken cancellationToken);
}
