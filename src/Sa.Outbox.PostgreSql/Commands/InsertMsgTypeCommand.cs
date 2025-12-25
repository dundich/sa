using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class InsertMsgTypeCommand(IPgDataSource dataSource, SqlOutboxTemplate template) : IInsertMsgTypeCommand
{
    public Task<int> Execute(long id, string typeName, CancellationToken cancellationToken)
    {
        return dataSource.ExecuteNonQuery(template.SqlInsertType
            , cmd => cmd
                .AddParamTypeId(id)
                .AddParamTypeName(typeName)
            , cancellationToken);
    }
}
