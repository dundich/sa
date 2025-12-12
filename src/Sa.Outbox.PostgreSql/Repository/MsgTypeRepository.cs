using System.Data;
using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed class MsgTypeRepository(IPgDataSource dataSource, SqlOutboxTemplate template) : IMsgTypeRepository
{
    public Task<int> Insert(long id, string typeName, CancellationToken cancellationToken)
    {
        return dataSource.ExecuteNonQuery(
            template.SqlInsertType
            , [new (SqlParam.TypeId, id), new (SqlParam.TypeName, typeName)]
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
