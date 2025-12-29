using Sa.Extensions;
using Sa.Outbox.Publication;
using Sa.Outbox.Support;


namespace Sa.Outbox.Delivery;


/// <summary>
/// Creates filters for outbox message queries.
/// </summary>
internal static class FilterFactory
{
    public static OutboxMessageFilter CreateFilter<TMessage>(
        int tenantId,
        string consumerGroupId,
        DateTimeOffset now,
        TimeSpan lookbackInterval,
        TimeSpan batchingWindow) where TMessage : IOutboxPayloadMessage
    {
        OutboxMessageTypeInfo ti = OutboxMessageTypeHelper.GetOutboxMessageTypeInfo<TMessage>();

        return new OutboxMessageFilter(
            TransactId: GenerateTransactId(),
            ConsumerGroupId: consumerGroupId,
            PayloadType: typeof(TMessage).Name,
            tenantId,
            ti.PartName,
            now.StartOfDay() - lookbackInterval,
            now - batchingWindow,
            now
        );
    }

    private static string GenerateTransactId()
        => $"{Environment.MachineName}-{Random.Shared.Next(0, 100000)}";
}
