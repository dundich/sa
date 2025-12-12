using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.TypeHashResolve;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class ExtendDeliveryCommand(
    IPgDataSource dataSource
    , IMsgTypeHashResolver hashResolver
    , SqlOutboxTemplate sqlTemplate
) : IExtendDeliveryCommand
{
    public async Task<int> Execute(TimeSpan lockExpiration, OutboxMessageFilter filter, CancellationToken cancellationToken)
    {
        long typeCode = await hashResolver.GetCode(filter.PayloadType, cancellationToken);

        long lockExpiresOn = (filter.ToDate + lockExpiration).ToUnixTimeSeconds();
        long fromDate = filter.FromDate.ToUnixTimeSeconds();
        long now = filter.ToDate.ToUnixTimeSeconds();

        return await dataSource.ExecuteNonQuery(sqlTemplate.SqlExtendDelivery,
        [
            new(SqlParam.TenantId, filter.TenantId)
            , new(SqlParam.ConsumerGroupId, filter.ConsumerGroupId)
            , new(SqlParam.FromDate, fromDate)
            , new(SqlParam.TransactId, filter.TransactId)
            , new(SqlParam.TypeName, typeCode)
            , new(SqlParam.LockExpiresOn, lockExpiresOn)
            , new(SqlParam.ToDate, now)
        ]
        , cancellationToken);
    }
}
