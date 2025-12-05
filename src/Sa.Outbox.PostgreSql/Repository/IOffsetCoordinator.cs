
namespace Sa.Outbox.PostgreSql.Repository;

internal sealed record GroupOffsetId(string OffsetId)
{
    public static GroupOffsetId Empty { get; } = new GroupOffsetId(string.Empty);
}

internal interface IOffsetCoordinator
{
    Task<GroupOffsetId> GetNextOffsetAndProcess(
        string groupId, 
        Func<GroupOffsetId, CancellationToken, Task<GroupOffsetId>> process, 
        CancellationToken cancellationToken = default);
}
