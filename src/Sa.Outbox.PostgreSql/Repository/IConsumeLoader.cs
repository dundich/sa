
namespace Sa.Outbox.PostgreSql.Repository;

public sealed record GroupOffset(string OffsetId)
{
    public static GroupOffset Empty { get; } = new GroupOffset("01KBQ8DYRBSQ11R20ZKRBYD2G9");
    public bool IsEmpty() => this == Empty;
}

public sealed record ConsumeTenantGroup(string ConsumerGroupId, int TenantId);


public sealed record LoadGroupResult(int CopiedRows, GroupOffset GroupOffset)
{
    public static LoadGroupResult Empty { get; } = new(0, GroupOffset.Empty);
    public bool IsEmpty() => CopiedRows <= 0 || GroupOffset.IsEmpty();
}


internal interface IConsumeLoader
{
    Task<LoadGroupResult> LoadGroup(OutboxMessageFilter filter, int batchSize, CancellationToken cancellationToken = default);
}
