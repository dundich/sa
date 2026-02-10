# Sa.Outbox.PostgreSql

An AOT library for implementing the **Transactional Outbox** pattern using PostgreSQL in .NET applications. Provides guaranteed message delivery with support for multi-tenancy, concurrent processing, and advanced consumer management.

## Quick Start

### Installation
```bash
dotnet add package Sa.Outbox.PostgreSql
```

### Configuration DI

```csharp
builder.Services
    // outbox
    .AddSaOutbox(builder => builder
        .WithTenants((_, ts) => ts.WithTenantIds(1, 2, 3))
        .WithDeliveries(b => b.AddDelivery<MyConsumer, MyMessage>())
    )
    // outbox pg
    .AddSaOutboxUsingPostgreSql(cfg => cfg
        .WithDataSource(ds => ds.WithConnectionString("Host=my_host;Database=my_db;Username=my_user;Password=my_password"))
        .WithOutboxSettings((_, settings) => settings.TableSettings.WithSchema("my_outbox"))
    )
)
```

### Publishing Messages

```csharp
public sealed record MyMessage(string PayloadId);

// Batch publishing for different tenants
await publisher.Publish([new MyMessage("#1"), new MyMessage("#2")], tenantId: 1);
```

### Message Processing

```csharp
sealed class MyConsumer : IConsumer<MyMessage>
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

## Key Features

### 1. Multi-Consumer Support
A single message type can be processed by multiple independent consumers. Each consumer has its own queue with tracked offset.

### 2. Multi-Tenancy
The library is designed with data isolation between tenants in mind:

```csharp
.WithTenants((_, ts) => ts
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
