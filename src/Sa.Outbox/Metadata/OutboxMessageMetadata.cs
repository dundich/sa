namespace Sa.Outbox.Metadata;


internal sealed record OutboxMessageMetadata(
    string PartName,
    Func<object, string> GetPayloadId)
{
    public static readonly OutboxMessageMetadata Empty = new("root", m =>
    {
        return (m is IOutboxPublishable msg) ? msg.GetPayloadId() : string.Empty;
    });
}
