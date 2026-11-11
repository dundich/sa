namespace Sa.Outbox.PostgreSql.Services;


/// <summary>Outcome of one task-queue load for a consumer group.</summary>
public sealed record LoadGroupResult
{
    /// <summary>Rows copied into the queue by the load.</summary>
    public int CopiedRows { get; init; }

    /// <summary>The message sequence number the next load starts from.</summary>
    public long NewOffset { get; init; }

    /// <summary>The result of a load that found nothing.</summary>
    public static LoadGroupResult Empty { get; } = new();

    /// <summary>Whether the load copied no rows.</summary>
    /// <returns><see langword="true"/> when nothing was loaded.</returns>
    public bool IsEmpty() => CopiedRows <= 0 || NewOffset == 0;
}


internal interface IOutboxTaskLoader
{
    /// <summary>
    /// Подгружаеи новые задания для консьюмера из таблицы вх. сообщений _msg$
    /// </summary>
    Task<LoadGroupResult> LoadNewTasksAsync(
        OutboxMessageFilter filter, int batchSize, CancellationToken cancellationToken = default);
}
