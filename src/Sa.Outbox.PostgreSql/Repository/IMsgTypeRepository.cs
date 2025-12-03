namespace Sa.Outbox.PostgreSql.Repository;

internal interface IMsgTypeRepository
{
    Task<int> Insert(long id, string typeName, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<(long id, string typeName)>> SelectAll(CancellationToken cancellationToken);
}
