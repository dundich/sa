namespace Sa.Outbox;

public interface IOutboxBuilder
{
    OutboxPublishSettings PublishSettings { get; }
    /// <summary>
    /// Configures the delivery settings for the outbox.
    /// </summary>
    /// <param name="build">An action to configure the delivery settings.</param>
    /// <returns>The current instance of the IOutboxSettingsBuilder.</returns>
    IOutboxBuilder WithDeliveries(Action<IDeliveryBuilder> build);

    /// <summary>
    /// Enables partitioning support for the outbox.
    /// </summary>
    /// <param name="configure">An action to configure the partitioning settings.</param>
    /// <returns>The current instance of the IOutboxSettingsBuilder.</returns>
    IOutboxBuilder WithPartitioningSupport(Action<IServiceProvider, PartitionalSettings> configure);
}