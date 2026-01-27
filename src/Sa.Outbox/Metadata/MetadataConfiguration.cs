namespace Sa.Outbox.Metadata;

internal sealed class MetadataConfiguration : IOutboxMessageMetadataBuilder, IOutboxMessageMetadataProvider
{
    private readonly Dictionary<Type, OutboxMessageMetadata> _metadata = [];

    private static readonly Func<object, string> s_Dummy = _ => string.Empty;

    private static readonly OutboxMessageMetadata s_Default = new("root", s_Dummy);


    public IOutboxMessageMetadataBuilder AddMetadata<T>(string partName, Func<T, string>? getPayloadId = null)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(partName))
        {
            throw new ArgumentException(
                "Part name cannot be null or whitespace",
                nameof(partName));
        }

        var messageType = typeof(T);

        Func<object, string> payloadIdGetter = getPayloadId != null
            ? obj => getPayloadId((T)obj)
            : s_Dummy;

        _metadata[messageType] = new OutboxMessageMetadata(
            PartName: partName,
            GetPayloadId: payloadIdGetter);

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


    internal void MergeFrom(MetadataConfiguration other)
    {
        foreach (var cmeta in other._metadata)
        {
            _metadata[cmeta.Key] = cmeta.Value;
        }
    }
}
