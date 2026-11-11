
namespace Sa.Outbox.PostgreSql.Commands;

internal interface IInsertMsgTypeCommand
{
    Task<int> ExecuteAsync(long id, string typeName, CancellationToken cancellationToken);
}
