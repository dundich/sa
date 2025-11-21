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
        DateTimeOffset now,
        TimeSpan lookbackInterval,
        int tenantId) where TMessage : IOutboxPayloadMessage
    {
        OutboxMessageTypeInfo ti = OutboxMessageTypeHelper.GetOutboxMessageTypeInfo<TMessage>();
        DateTimeOffset fromDate = now.StartOfDay() - lookbackInterval;

        return new OutboxMessageFilter(
            TransactId: GenerateTransactId(),
            PayloadType: typeof(TMessage).Name,
            tenantId,
            ti.PartName,
            fromDate,
            now
        );
    }

    private static string GenerateTransactId() => $"{Environment.MachineName}-{Guid.NewGuid():N}";
}
