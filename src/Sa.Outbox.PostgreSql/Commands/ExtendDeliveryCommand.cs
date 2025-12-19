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
        var lockExpiresOn = filter.NowDate + lockExpiration;

        return await dataSource.ExecuteNonQuery(sqlTemplate.SqlExtendDelivery, cmd => cmd
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
