using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class InsertMsgTypeCommand(IPgDataSource dataSource, SqlOutboxBuilder template) : IInsertMsgTypeCommand
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
