
namespace Sa.Outbox.PostgreSql.Services;


public sealed record LoadGroupResult(int CopiedRows, Guid NewOffset)
{
    public static LoadGroupResult Empty { get; } = new(0, Guid.Empty);
    public bool IsEmpty() => CopiedRows <= 0 || NewOffset == Guid.Empty;
}


internal interface IOutboxTaskLoader
{
    Task<LoadGroupResult> LoadNewTasks(OutboxMessageFilter filter, int batchSize, CancellationToken cancellationToken = default);
}
