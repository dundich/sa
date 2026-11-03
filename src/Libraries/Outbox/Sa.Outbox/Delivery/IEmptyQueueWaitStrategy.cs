namespace Sa.Outbox.Delivery;

/// <summary>
/// Computes how long a consumer group should wait before its next poll when the previous poll
/// found nothing to deliver.
/// <para>
/// The outbox delivery job is timer-driven by <c>Sa.Schedule</c>. On every tick it drains the
/// backlog greedily and then returns; the wait until the following tick is what this strategy
/// controls. Implementations let a consumer back off while its queue stays empty, instead of
/// hammering the database on every fixed <see cref="OutboxConsumerSettings.Interval"/>.
/// </para>
/// <para>
/// This is unrelated to <see cref="IDeliveryBackoffStrategy"/>, which spaces out retries of a
/// single failed message.
/// </para>
/// </summary>
public interface IEmptyQueueWaitStrategy
{
    /// <summary>
    /// Returns the delay before the next poll of a consumer group whose last polls found nothing.
    /// </summary>
    /// <param name="baseInterval">
    /// The configured <see cref="OutboxConsumerSettings.Interval"/> — the interval used while work
    /// is flowing. Implementations must never return less than this.
    /// </param>
    /// <param name="maxInterval">
    /// The configured <see cref="OutboxConsumerSettings.IdleMaxInterval"/> — the upper bound of the
    /// back-off. <see cref="TimeSpan.Zero"/> means adaptive back-off is disabled and
    /// <paramref name="baseInterval"/> must be returned unchanged.
    /// </param>
    /// <param name="consecutiveEmptyPolls">
    /// The number of polls in a row that found no work, including the one that just finished
    /// (1 on the first empty poll).
    /// </param>
    /// <returns>The delay to apply before the next poll.</returns>
    TimeSpan GetNextWait(TimeSpan baseInterval, TimeSpan maxInterval, int consecutiveEmptyPolls);
}
