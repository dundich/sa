namespace Sa.Outbox.Metadata;


internal sealed class OutboxMessageMetadata
{
    /// <summary>
    /// The caller-supplied <c>Func&lt;TMessage, string&gt;</c>, kept as a delegate rather than
    /// wrapped in <c>Func&lt;object, string&gt;</c>: the object-shaped wrapper boxed every value
    /// payload on each publish, which is especially expensive for large structs.
    /// </summary>
    private readonly Delegate? _getPayloadId;

    public OutboxMessageMetadata(string partName, Delegate? getPayloadId)
    {
        PartName = partName;
        _getPayloadId = getPayloadId;
    }

    public string PartName { get; }

    public string GetPayloadId<TMessage>(TMessage message)
        => (_getPayloadId as Func<TMessage, string>)?.Invoke(message)
           ?? PublishableId<TMessage>.Get(message);

    public static readonly OutboxMessageMetadata Empty = new("root", getPayloadId: null);

    /// <summary>
    /// Fallback for a type without explicit metadata. The interface test is resolved once per
    /// closed <typeparamref name="TMessage"/>; a value type that does not implement
    /// <see cref="IOutboxPublishable"/> never reaches the boxed interface cast.
    /// </summary>
    private static class PublishableId<TMessage>
    {
        private static readonly Func<TMessage, string>? Getter =
            typeof(IOutboxPublishable).IsAssignableFrom(typeof(TMessage))
                ? static message => ((IOutboxPublishable)message!).GetPayloadId()
                : null;

        public static string Get(TMessage message) => Getter?.Invoke(message) ?? string.Empty;
    }
}
