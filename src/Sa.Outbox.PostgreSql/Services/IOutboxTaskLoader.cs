namespace Sa.Outbox.PostgreSql.Services;


public sealed record LoadGroupResult(int CopiedRows, long NewOffset)
{
    public static LoadGroupResult Empty { get; } = new(0, 0);
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