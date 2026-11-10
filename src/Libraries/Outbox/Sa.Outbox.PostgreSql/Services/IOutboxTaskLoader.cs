namespace Sa.Outbox.PostgreSql.Services;


/// <summary>Outcome of one task-queue load for a consumer group.</summary>
/// <param name="CopiedRows">Rows copied into the queue by the load.</param>
/// <param name="NewOffset">The message sequence number the next load starts from.</param>
public sealed record LoadGroupResult(int CopiedRows, long NewOffset)
{
    /// <summary>The result of a load that found nothing.</summary>
    public static LoadGroupResult Empty { get; } = new(0, 0);

    /// <summary>Whether the load copied no rows.</summary>
    /// <returns><see langword="true"/> when nothing was loaded.</returns>
    public bool IsEmpty() => CopiedRows <= 0 || NewOffset == 0;
}


internal interface IOutboxTaskLoader
{
    /// <summary>
    /// Подгружаеи новые задания для консьюмера из таблицы вх. сообщений _msg$
    /// </summary>
    Task<LoadGroupResult> LoadNewTasks(
        OutboxMessageFilter filter, int batchSize, CancellationToken cancellationToken = default);
}
