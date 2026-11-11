namespace Sa.Outbox.PostgreSql.Services;

internal interface IOutboxMsgTypeRepository
{
    Task<int> InsertAsync(long id, string typeName, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<(long id, string typeName)>> SelectAll(CancellationToken cancellationToken);
}
