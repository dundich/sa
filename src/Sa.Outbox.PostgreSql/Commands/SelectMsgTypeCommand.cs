using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql.Commands;

internal class SelectMsgTypeCommand(
    IPgDataSource dataSource,
    SqlOutboxTemplate template,
    NpqsqlOutboxReader outboxReader
    ) : ISelectMsgTypeCommand
{
    public async Task<IReadOnlyCollection<(long id, string typeName)>> Execute(CancellationToken cancellationToken)
    {
        return await dataSource.ExecuteReaderList(template.SqlSelectType,
            reader => (id: outboxReader.Type.GetTypeId(reader), typeName: outboxReader.Type.GetTypeName(reader))
        , cancellationToken);
    }
}
