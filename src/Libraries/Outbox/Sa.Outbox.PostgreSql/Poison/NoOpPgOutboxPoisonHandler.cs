namespace Sa.Outbox.PostgreSql.Poison;

/// <summary>
/// The default <see cref="IPgOutboxPoisonHandler"/>: keeps the built-in strategy.
/// </summary>
/// <remarks>
/// <see cref="PgOutboxPoisonAction.Default"/> preserves the behaviour from before the handler existed:
/// the permanent failure is persisted in the task row and <c>__error$</c>. Replace the handler through
/// <c>WithPoisonHandler</c> to take over persistence (<see cref="PgOutboxPoisonAction.Handled"/>) or to
/// add a side channel while keeping the default.
/// </remarks>
internal sealed class NoOpPgOutboxPoisonHandler : IPgOutboxPoisonHandler
{
    public ValueTask<PgOutboxPoisonAction> Handle(PgOutboxPoisonMessage message, CancellationToken cancellationToken)
        => ValueTask.FromResult(PgOutboxPoisonAction.Default);
}
