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
    {
        // One string instead of two: the interpolation used to call Guid.ToString("N") and then
        // concatenate, allocating an intermediate 32-character string per filter (i.e. per tenant
        // per delivery iteration). TryFormat writes straight into the destination.
        Guid guid = Guid.CreateVersion7();
        string machine = Environment.MachineName;

        return string.Create(
            machine.Length + 1 + 32,
            (machine, guid),
            static (span, state) =>
            {
                state.machine.AsSpan().CopyTo(span);
                span[state.machine.Length] = '-';
                state.guid.TryFormat(span[(state.machine.Length + 1)..], out _, "N");
            });
    }
}
