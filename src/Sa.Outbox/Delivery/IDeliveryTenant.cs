namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages for a specific tenant with locking and delivery.
/// </summary>
internal interface IDeliveryTenant
{
    /// <summary>
    /// Acquires a tenant-level lock, retrieves pending outbox messages, and delivers them to the appropriate consumer.
    /// </summary>
    /// <typeparam name="TMessage">The type of the messages being delivered.</typeparam>
    /// <param name="tenantId">The identifier of the tenant whose messages to process.</param>
    /// <param name="settings">Runtime delivery settings for this tenant's processing scope.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of messages successfully processed.</returns>
    Task<int> ProcessInTenant<TMessage>(
            int tenantId,
            OutboxConsumerSettings settings,
            CancellationToken cancellationToken);
}
