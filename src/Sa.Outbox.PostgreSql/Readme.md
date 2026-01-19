# Sa.Outbox.PostgreSql

An AOT library for implementing the **Transactional Outbox** pattern using PostgreSQL in .NET applications. Provides guaranteed message delivery with support for multi-tenancy, concurrent processing, and advanced consumer management.

## Quick Start

### Installation
```bash
dotnet add package Sa.Outbox.PostgreSql
```

### Configuration DI

```csharp
ConfigureServices(services => services
    // outbox
    .AddOutbox(builder => builder
        .WithTenantSettings((_, ts) => ts.WithTenantIds(1, 2, 3))
        .WithDeliveries(builder => builder
            .AddDelivery<MyConsumer, MyMessage>((_, settings) =>
            {
                settings.ScheduleSettings.WithIntervalSeconds(5);
            })
        )
    )
    // outbox pg
    .AddOutboxUsingPostgreSql(cfg => cfg
        .WithDataSource(ds => ds.WithConnectionString(connectionString))
        .WithOutboxSettings((_, settings) =>
        {
            settings.TableSettings.WithSchema("my_outbox");
            settings.ConsumeSettings.WithMinOffset<MyConsumer>(DateTimeOffset.Now);
        })
        .WithMessageSerializer(...)
    )
)
```

### Publishing Messages

```csharp
public sealed record MyMessage(string PayloadId, int TenantId = 0) : IOutboxPayloadMessage
{
    public static string PartName => "root";
}

// Batch publishing for different tenants
await publisher.Publish([
    new MyMessage("#1", 1),
    new MyMessage("#2", 2),
    new MyMessage("#3", 3)
]);
```

### Message Processing

```csharp
public class MyConsumer : IConsumer<MyMessage>
{
    public async ValueTask Consume(
        ConsumerGroupSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<MyMessage>> messages,
        CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            foreach (var message in messages.Span)
            {
                message.Ok("Message processed successfully.");
            }
        }
}
```

## Messages

All messages for publication must implement the interface:

```csharp
public interface IOutboxHasPart
{
    /// <summary>
    /// Gets the logical identifier of the partition associated with this type.
    /// </summary>
    /// <example>"orders", "notifications"</example>
    static abstract string PartName { get; }
}

/// <summary>
/// This interface defines the properties that any Outbox payload message must implement.
/// </summary>
public interface IOutboxPayloadMessage : IOutboxHasPart
{
    /// <summary>
    /// Gets the unique identifier for the payload.
    /// </summary>
    string PayloadId { get; }

    /// <summary>
    /// Gets the identifier for the tenant associated with the payload.
    /// </summary>
    int TenantId { get; }
}
```

## Key Features

### 1. Multi-Consumer Support
A single message type can be processed by multiple independent consumers. Each consumer has its own queue with tracked offset.

### 2. Multi-Tenancy
The library is designed with data isolation between tenants in mind:

```csharp
.WithTenantSettings((_, ts) => ts
    .WithTenantIds(1, 2, 3)                     // Explicit tenant specification
    .WithAutoDetect()                           // Or automatic detection
    .WithTenantDetector<TenantDetector>()       // Custom tenant detector
    .WithTenantParallelProcessing(3)            // Can be processed concurrently
)
```

### 3. Bulk Operations
- **Batch publishing**: For maximum performance
- **Batch processing**: Consumers receive messages in batches
- **Batch acknowledgment**: Mass status updates

### 4. Extended Status System
- **`Ok()`** - Successful processing
- **`Error(Exception)`** - Permanent error
- **`Warn(Exception)`** - Temporary error with retry
- **`Postpone(TimeSpan)`** - Delay processing
- **`Aborted(string)`** - Skip with specified reason
- e.t.c

### 5. Pull Model with Static and Dynamic Management
```csharp
// DI
settings.ConsumeSettings
    .WithIntervalSeconds(5)
    .WithMaxDeliveryAttempts(3)
    .WithBatchingWindow(TimeSpan.FromMinutes(5))
    .WithLockDuration(TimeSpan.FromMinutes(10));

   // Dynamic management during runtime
    public async ValueTask Consume(ConsumerGroupSettings settings,...
        settings.ConsumeSettings.WithMaxProcessingIterations(100);
```

## Database Architecture

### Table Structure

```
📂 outbox (schema)
├── 📄 outbox__msg$     - Source messages (read-only)
├── 📄 outbox           - Task queues for consumers (read-write)
├── 📄 outbox__log$     - Delivery history (read-only)
├── 📄 outbox__error$   - Permanent errors (read-only)
├── 📄 outbox__type$    - Message type registration (read-only)
└── 📄 outbox__offset$  - Offsets for consumer groups (read-write)
```

### Implementation Details

- **BINARY COPY** for bulk message insertion into `outbox__msg$`
- **SKIP LOCKED** for concurrent processing of `outbox`
- **Advisory Locks** for coordinating `outbox__offset$` offsets
- **Batch operations** to minimize round-trips

### Table Settings
```csharp
.WithOutboxSettings((_, settings) =>
{
    // Schema and tables
    settings.TableSettings
        .WithSchema("my_schema")
        // Table fields
        .TaskQueue.Fields = {
            TaskId = "id",
            TenantId = "client_id"
        };

    // Automatic partition migration
    settings.MigrationSettings
        .WithForwardDays(2);

    // Automatic partition cleanup
    settings.CleanupSettings
        .WithDropPartsAfterRetention(TimeSpan.FromDays(30));
})
```

## Requirements

- **.NET 10.0** or higher
- **PostgreSQL 15+**

## License

MIT