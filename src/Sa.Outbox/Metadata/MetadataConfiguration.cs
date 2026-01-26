namespace Sa.Outbox.Metadata;

internal sealed class MetadataConfiguration : IOutboxMessageMetadataBuilder, IOutboxMessageMetadataProvider
{
    private readonly Dictionary<Type, OutboxMessageMetadata> _metadata = [];

    private static readonly Func<object, string> s_Dummy = _ => string.Empty;

    private static readonly OutboxMessageMetadata s_Default = new("root", s_Dummy);


    public IOutboxMessageMetadataBuilder AddMetadata<T>(string partName, Func<T, string>? getPayloadId = null)
        where T : class
    {
        var messageType = typeof(T);

        _metadata[messageType] = new OutboxMessageMetadata(
            PartName: partName,
            GetPayloadId: (obj) =>
            {
                if (getPayloadId != null)
                {
                    return getPayloadId((T)obj);
                }
                return s_Dummy(obj);
            });

        return this;
    }


    public OutboxMessageMetadata GetMetadata(Type messageType)
    {
        if (_metadata.TryGetValue(messageType, out var metadata))
        {
            return metadata;
        }

        return s_Default;
    }


    internal void Assign(MetadataConfiguration configuration)
    {
        foreach (var cmeta in configuration._metadata)
        {
            _metadata[cmeta.Key] = cmeta.Value;
        }
    }
}
