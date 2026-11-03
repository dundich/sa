namespace Sa.Outbox.PostgreSql.Poison;

/// <summary>
/// What the provider should do with a permanent failure after
/// <see cref="IPgOutboxPoisonHandler"/> has seen it.
/// </summary>
public enum PgOutboxPoisonAction
{
    /// <summary>
    /// Keep the built-in strategy: insert the de-duplicated <c>__error$</c> row and finalize the task
    /// with its <c>error_id</c>. This is the default and preserves the behaviour from before the
    /// handler existed.
    /// </summary>
    Default = 0,

    /// <summary>
    /// The handler took responsibility for the failure: skip the <c>__error$</c> insert. The task is
    /// still finalized as <c>Error</c>/<c>MaximumAttemptsError</c>, but with <c>error_id = NULL</c>.
    /// </summary>
    Handled = 1,
}
