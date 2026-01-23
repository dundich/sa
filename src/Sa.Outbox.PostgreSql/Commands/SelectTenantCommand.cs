using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class SelectTenantCommand(
    IPgDataSource dataSource,
    SqlOutboxBuilder sql,
    NpqsqlOutboxReader outboxReader
    ) : ISelectTenantCommand
{
    public async Task<IReadOnlyCollection<int>> Execute(CancellationToken cancellationToken)
    {
        return await dataSource.ExecuteReaderList(sql.SqlSelectTetant,
            reader => outboxReader.Message.GetTenantId(reader),
            cancellationToken);
    }
}
