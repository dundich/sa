namespace Sa.Outbox.Delivery;

/// <summary>
/// One rented message as the provider hands it to the delivery pipeline: the payload with its
/// message-side part, the delivery record, and the provider's own rent token.
/// </summary>
/// <remarks>
/// <para>
/// Flat on purpose. The previous shape nested <c>OutboxMessage&lt;TMessage&gt;</c>, which duplicated
/// the message-side <see cref="OutboxPartInfo"/> that the delivery record also carries, and buried
/// the two fields a consumer actually needs (<see cref="PayloadId"/>, <see cref="Payload"/>).
/// </para>
/// <para>
/// <see cref="PartInfo"/> is the <em>message</em> part (tenant, part, message creation time) — the
/// same value the publishing side produced. <see cref="OutboxTaskDeliveryInfo.PartInfo"/> is a
/// different value: the <em>task</em> part (tenant, consumer group, task creation time).
/// </para>
/// <para>
/// <see cref="RentToken"/> is the provider's opaque rent identity (see <see cref="OutboxRentToken"/>);
/// the core stores it in the context and hands the same contexts back to
/// <see cref="Sa.Outbox.PlugServices.IOutboxDeliveryManager.ReturnDelivery{TMessage}"/>, where the
/// provider reads it back through <see cref="IOutboxContext.RentToken"/>.
/// </para>
/// </remarks>
/// <param name="OutboxId">The provider's public message identity (PostgreSQL: the message uuid; Redis: the packed stream id).</param>
/// <param name="PayloadId">The application-supplied payload id from the publishing side.</param>
/// <param name="Payload">The deserialized payload.</param>
/// <param name="PartInfo">The message part (tenant, part, message creation time).</param>
/// <param name="DeliveryInfo">The task/delivery record.</param>
/// <param name="RentToken">The provider's opaque rent identity.</param>
/// <typeparam name="TMessage">The type of the message payload.</typeparam>
public readonly record struct OutboxDeliveryMessage<TMessage>(
    Guid OutboxId,
    string PayloadId,
    TMessage Payload,
    OutboxPartInfo PartInfo,
    OutboxTaskDeliveryInfo DeliveryInfo,
    OutboxRentToken RentToken = default);
