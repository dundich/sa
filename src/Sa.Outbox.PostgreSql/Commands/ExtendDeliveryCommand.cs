using Npgsql;
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

        long lockExpiresOn = (filter.NowDate + lockExpiration).ToUnixTimeSeconds();
        long fromDate = filter.FromDate.ToUnixTimeSeconds();
        long now = filter.NowDate.ToUnixTimeSeconds();

        return await dataSource.ExecuteNonQuery(sqlTemplate.SqlExtendDelivery,
        [
            new NpgsqlParameter<int>(SqlParam.TenantId, filter.TenantId)
            , new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, filter.ConsumerGroupId)
            , new NpgsqlParameter<long>(SqlParam.FromDate, fromDate)
            , new NpgsqlParameter<string>(SqlParam.TransactId, filter.TransactId)
            , new NpgsqlParameter<long>(SqlParam.TypeName, typeCode)
            , new NpgsqlParameter<long>(SqlParam.LockExpiresOn, lockExpiresOn)
            , new NpgsqlParameter<long>(SqlParam.NowDate, now)
        ]
        , cancellationToken);
    }
}
