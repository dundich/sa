using Sa.Data.PostgreSql;
using System.Data;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed class MsgTypeRepository(IPgDataSource dataSource, SqlOutboxTemplate template) : IMsgTypeRepository
{
    public Task<int> Insert(long id, string typeName, CancellationToken cancellationToken)
    {
        return dataSource.ExecuteNonQuery(
            template.SqlInsertType
            , [new ("type_id", id), new ("type_name", typeName)]
            , cancellationToken);
    }

    public async Task<IReadOnlyCollection<(long id, string typeName)>> SelectAll(CancellationToken cancellationToken)
    {
        return await dataSource.ExecuteReaderList(template.SqlSelectType,
            reader =>
                (id: reader.GetInt64("type_id"), typeName: reader.GetString("type_name"))
        , cancellationToken);
    }
}
