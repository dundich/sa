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
        long now = filter.ToDate.ToUnixTimeSeconds();
        long lockExpiresOn = (filter.ToDate + lockExpiration).ToUnixTimeSeconds();
        long fromDate = filter.FromDate.ToUnixTimeSeconds();

        return await dataSource.ExecuteNonQuery(sqlTemplate.SqlExtendDelivery,
        [
            new(CachedSqlParamNames.TenantId, filter.TenantId)
            , new(CachedSqlParamNames.MsgPart, filter.Part)
            , new(CachedSqlParamNames.FromDate, fromDate)
            , new(CachedSqlParamNames.DeliveryTransactId, filter.TransactId)
            , new(CachedSqlParamNames.TypeName, typeCode)
            , new(CachedSqlParamNames.LockExpiresOn, lockExpiresOn)
            , new(CachedSqlParamNames.NowDate, now)
        ]
        , cancellationToken);
    }
}
