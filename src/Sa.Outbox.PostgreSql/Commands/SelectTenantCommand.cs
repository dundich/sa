using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class SelectTenantCommand(
    IPgDataSource dataSource,
    SqlOutboxBuilder sql) : ISelectTenantCommand
{
    public async Task<IReadOnlyCollection<int>> Execute(CancellationToken cancellationToken)
    {
        try
        {
            // SqlSelectTenant projects exactly one column, so its ordinal is always 0 —
            // no per-row GetOrdinal scan and no settings lookup are needed here.
            return await dataSource.ExecuteReaderList(sql.SqlSelectTenant,
                reader => reader.GetInt32(0),
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }
}
