using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed class MsgTypeRepository(IPgDataSource dataSource, SqlOutboxTemplate template) : IMsgTypeRepository
{
    public Task<int> Insert(long id, string typeName, CancellationToken cancellationToken)
    {
        return dataSource.ExecuteNonQuery(template.SqlInsertType
            , cmd => cmd
                .AddParamTypeId(id)
                .AddParamTypeName(typeName)
            , cancellationToken);
    }

    public async Task<IReadOnlyCollection<(long id, string typeName)>> SelectAll(CancellationToken cancellationToken)
    {
        return await dataSource.ExecuteReaderList(template.SqlSelectType,
            reader => (id: reader.GetTypeId(), typeName: reader.GetTypeName())
        , cancellationToken);
    }
}
