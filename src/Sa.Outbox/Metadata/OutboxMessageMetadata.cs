namespace Sa.Outbox.Metadata;


internal sealed record OutboxMessageMetadata(
    string PartName,
    Func<object, string> GetPayloadId);
