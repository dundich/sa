
namespace Sa.Outbox.PostgreSql.Repository;

internal sealed record GroupOffsetId(string OffsetId)
{
    public static GroupOffsetId Empty { get; }= new GroupOffsetId("01KBQ8DYRBSQ11R20ZKRBYD2G9");
}

internal interface IOffsetCoordinator
{
    Task<GroupOffsetId> GetNextOffsetAndProcess(
        string groupId,
        int tenantId,
        Func<GroupOffsetId, CancellationToken, Task<GroupOffsetId>> process,
        CancellationToken cancellationToken = default);
}
