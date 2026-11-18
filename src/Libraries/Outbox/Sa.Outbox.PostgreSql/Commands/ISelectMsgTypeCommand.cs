
namespace Sa.Outbox.PostgreSql.Commands;

internal interface ISelectMsgTypeCommand
{
    Task<IReadOnlyCollection<(long id, string typeName)>> ExecuteAsync(CancellationToken cancellationToken);
}
