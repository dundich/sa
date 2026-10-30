
namespace Sa.Outbox.PostgreSql.Commands;

internal interface ISelectMsgTypeCommand
{
    Task<IReadOnlyCollection<(long id, string typeName)>> Execute(CancellationToken cancellationToken);
}
