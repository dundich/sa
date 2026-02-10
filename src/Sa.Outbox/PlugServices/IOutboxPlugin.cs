namespace Sa.Outbox.PlugServices;

public interface IOutboxPlugin : IAsyncDisposable
{
    string Name { get; }
    string Version { get; }
    string Provider { get; } // "Postgres", "SqlServer", "Redis", etc.

    IOutboxBulkWriter BulkWriter { get; }
    IOutboxDeliveryManager DeliveryManager { get; }
    IOutboxTenantDetector TenantDetector { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}
