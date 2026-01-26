namespace Sa.Outbox.Metadata;

internal sealed class MetadataConfiguration : IOutboxMessageMetadataBuilder, IOutboxMessageMetadataProvider
{
    private readonly Dictionary<Type, OutboxMessageMetadata> _metadata = [];

    private static readonly OutboxMessageMetadata s_Default = new("root", _ => string.Empty);

    public IOutboxMessageMetadataBuilder AddMetadata<T>(string partName, Func<T, string> getPayloadId)
        where T : class
    {
        var messageType = typeof(T);

        _metadata[messageType] = new OutboxMessageMetadata(
            partName,
            obj => getPayloadId((T)obj)
        );

        return this;
    }


    public OutboxMessageMetadata GetMetadata(Type messageType)
        => _metadata.TryGetValue(messageType, out var metadata)
            ? metadata
            : s_Default;


    internal void Assign(MetadataConfiguration configuration)
    {
        foreach (var cmeta in configuration._metadata)
        {
            _metadata[cmeta.Key] = cmeta.Value;
        }
    }
}
