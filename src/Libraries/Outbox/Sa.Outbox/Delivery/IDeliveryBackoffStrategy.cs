namespace Sa.Outbox.Delivery;

/// <summary>
/// Defines a strategy for calculating the backoff delay before the next delivery attempt of a
/// message.
/// <para>
/// Only the delay is computed here — the retry itself is orchestrated by
/// <see cref="DeliveryCourier"/>, which applies the returned value as the message's
/// <c>PostponeDelay</c> when the consumer throws. This is deliberately unrelated to
/// <see cref="OutboxConsumerSettings.RetryCountOnError"/>, which retries the whole delivery
/// <em>job</em>, and to <c>Sa.Data.PostgreSql.PgRetryStrategy</c>, which retries a database
/// operation.
/// </para>
/// </summary>
public interface IDeliveryBackoffStrategy
{
    /// <summary>
    /// Calculates the backoff delay for the next retry attempt.
    /// </summary>
    /// <param name="attemptNumber">The current attempt number (1-based). Higher attempts should typically yield longer delays.</param>
    /// <returns>The time span to wait before the next retry.</returns>
    TimeSpan GetBackoff(int attemptNumber);
}
