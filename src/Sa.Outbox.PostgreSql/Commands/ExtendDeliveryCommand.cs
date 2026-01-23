using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.TypeResolve;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class ExtendDeliveryCommand(
    IPgDataSource dataSource
    , IOutboxTypeResolver hashResolver
    , SqlOutboxBuilder sql
) : IExtendDeliveryCommand
{
    public async Task<int> Execute(TimeSpan lockExpiration, OutboxMessageFilter filter, CancellationToken cancellationToken)
    {
        long typeCode = await hashResolver.GetHashCode(filter.PayloadType, cancellationToken);
        var lockExpiresOn = filter.NowDate + lockExpiration;

        return await dataSource.ExecuteNonQuery(sql.SqlExtendDelivery, cmd => cmd
            .AddParamTenantId(filter.TenantId)
            .AddParamConsumerGroupId(filter.ConsumerGroupId)
            .AddParamFromDate(filter.FromDate)
            .AddParamNowDate(filter.NowDate)
            .AddParamTransactId(filter.TransactId)
            .AddParamLockExpiresOn(lockExpiresOn)
            .AddParamTypeId(typeCode)
        , cancellationToken);
    }
}
