
namespace Sa.Outbox.PostgreSql.Commands;

internal interface IInsertMsgTypeCommand
{
    Task<int> Execute(long id, string typeName, CancellationToken cancellationToken);
}
