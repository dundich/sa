namespace Sa.Outbox.PlugServices;

/// <summary>
/// One message inside an <see cref="OutboxMessageBatch{TMessage}"/>: the payload and its
/// caller-supplied identifier. A struct rather than the <see cref="OutboxMessage{TMessage}"/>
/// record, so a bulk publish does not allocate one wrapper object per message.
/// </summary>
/// <typeparam name="TMessage">The type of the payload.</typeparam>
public readonly record struct OutboxMessageBatchItem<TMessage>(
    string PayloadId,
    TMessage Payload);

/// <summary>
/// A publish batch with one shared <see cref="PartInfo"/>. Every message of a publish call has
/// the same tenant, part and timestamp, so the part is stored once for the batch instead of being
/// repeated on every message wrapper.
/// </summary>
/// <typeparam name="TMessage">The type of the payload.</typeparam>
public readonly struct OutboxMessageBatch<TMessage>
{
    public OutboxMessageBatch(
        ReadOnlyMemory<OutboxMessageBatchItem<TMessage>> messages,
        OutboxPartInfo partInfo)
    {
        Messages = messages;
        PartInfo = partInfo;
    }

    /// <summary>The messages of this batch, in publish order.</summary>
    public ReadOnlyMemory<OutboxMessageBatchItem<TMessage>> Messages { get; }

    /// <summary>The part shared by every message of this batch.</summary>
    public OutboxPartInfo PartInfo { get; }

    /// <summary>Number of messages in the batch.</summary>
    public int Count => Messages.Length;

    /// <summary>Whether the batch carries no messages.</summary>
    public bool IsEmpty => Messages.IsEmpty;
}
