using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class SelectMsgTypeCommand(
    IPgDataSource dataSource,
    SqlOutboxBuilder sql,
    NpgsqlOutboxReader outboxReader) : ISelectMsgTypeCommand
{
    public async Task<IReadOnlyCollection<(long id, string typeName)>> Execute(CancellationToken cancellationToken)
    {
        return await dataSource.ExecuteReaderList(sql.SqlSelectType,
            reader => (id: outboxReader.Type.GetTypeId(reader), typeName: outboxReader.Type.GetTypeName(reader))
        , cancellationToken);
    }
}
