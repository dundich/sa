using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Services;


internal sealed class OutboxMsgTypeRepository(
    IInsertMsgTypeCommand insertCmd,
    ISelectMsgTypeCommand selectCmd
    ) : IOutboxMsgTypeRepository
{
    public Task<int> InsertAsync(long id, string typeName, CancellationToken cancellationToken)
        => insertCmd.ExecuteAsync(id, typeName, cancellationToken);

    public Task<IReadOnlyCollection<(long id, string typeName)>> SelectAll(CancellationToken cancellationToken)
        => selectCmd.ExecuteAsync(cancellationToken);
}
