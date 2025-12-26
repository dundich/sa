using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed class OutboxMsgTypeRepository(
    IInsertMsgTypeCommand insertCmd,
    ISelectMsgTypeCommand selectCmd
    ) : IOutboxMsgTypeRepository
{
    public Task<int> Insert(long id, string typeName, CancellationToken cancellationToken) 
        => insertCmd.Execute(id, typeName, cancellationToken);

    public Task<IReadOnlyCollection<(long id, string typeName)>> SelectAll(CancellationToken cancellationToken) 
        => selectCmd.Execute(cancellationToken);
}
