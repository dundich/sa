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
        long now = filter.NowDate.ToUnixTimeSeconds();
        long lockExpiresOn = (filter.NowDate + lockExpiration).ToUnixTimeSeconds();
        long fromDate = filter.FromDate.ToUnixTimeSeconds();

        return await dataSource.ExecuteNonQuery(sqlTemplate.SqlExtendDelivery,
        [
            new("tenant", filter.TenantId)
            , new("part", filter.Part)
            , new("from_date", fromDate)
            , new("transact_id", filter.TransactId)
            , new("payload_type", typeCode)
            , new("lock_expires_on", lockExpiresOn)
            , new("now", now)
        ]
        , cancellationToken);
    }
}
