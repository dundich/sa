using Sa.Extensions;
using Sa.Outbox.Metadata;


namespace Sa.Outbox.Delivery;


/// <summary>
/// Creates filters for outbox message queries.
/// </summary>
internal sealed class FilterFactory(IOutboxMessageMetadataProvider metadata)
{
    public OutboxMessageFilter CreateFilter<TMessage>(
        int tenantId,
        string consumerGroupId,
        DateTimeOffset now,
        TimeSpan lookbackInterval,
        TimeSpan batchingWindow)
    {
        return new OutboxMessageFilter(
            TransactId: GenerateTransactId(),
            ConsumerGroupId: consumerGroupId,
            PayloadType: typeof(TMessage).Name,
            TenantId: tenantId,
            Part: metadata.GetMetadata<TMessage>().PartName,
            FromDate: now.StartOfDay() - lookbackInterval,
            ToDate: now - batchingWindow,
            NowDate: now
        );
    }

    private static string GenerateTransactId()
        => $"{Environment.MachineName}-{Random.Shared.Next(0, 100000)}";
}
