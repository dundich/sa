
namespace Sa.Outbox.PostgreSql.Repository;

public sealed record GroupOffset(string OffsetId)
{
    public static GroupOffset Empty { get; } = new GroupOffset(CachedSqlParamNames.EmptyOffset);
}

public sealed record ConsumeTenantGroup(string ConsumerGroupId, int TenantId);


public sealed record LoadGroupResult(int CopiedRows, GroupOffset GroupOffset)
{
    public static LoadGroupResult Empty { get; } = new(0, GroupOffset.Empty);
}


internal interface IConsumeLoader
{
    Task<LoadGroupResult> LoadConsumerGroup(OutboxMessageFilter filter, int batchSize, CancellationToken cancellationToken = default);
}
