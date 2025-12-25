using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed class MsgTypeRepository(
    IInsertMsgTypeCommand insertCmd,
    ISelectMsgTypeCommand selectCmd
    ) : IMsgTypeRepository
{
    public Task<int> Insert(long id, string typeName, CancellationToken cancellationToken)
    {
        return insertCmd.Execute(id, typeName, cancellationToken);
    }

    public Task<IReadOnlyCollection<(long id, string typeName)>> SelectAll(CancellationToken cancellationToken)
    {
        return selectCmd.Execute(cancellationToken);
    }
}
