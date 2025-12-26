using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class SelectTenantCommand(
    IPgDataSource dataSource,
    SqlOutboxTemplate template,
    NpqsqlOutboxReader outboxReader
    ) : ISelectTenantCommand
{
    public async Task<IReadOnlyCollection<int>> Execute(CancellationToken cancellationToken)
    {
        return await dataSource.ExecuteReaderList(template.SqlSelectTetant,
            reader => outboxReader.Message.GetTenantId(reader),
            cancellationToken);
    }
}