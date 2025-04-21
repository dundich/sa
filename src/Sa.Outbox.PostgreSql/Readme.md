# Sa.Outbox.PostgreSql

Designed for implementing the Outbox pattern using PostgreSQL, which is used to ensure reliable message delivery in distributed systems. It helps prevent message loss and guarantees that messages will be processed even in the event of failures.

## Features
- Reliable message delivery: Ensures that messages are stored in the database until they are successfully processed.
- Parallel processing: Enables messages to be processed in parallel, increasing system performance.
- Flexibility: Supports various types of messages and their handlers.
- Tenant support: Allows for even distribution of load.

## Examples

### Configuration

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.PostgreSql;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOutbox(builder =>
        {
            builder.WithDeliveries(deliveryBuilder =>
            {
                deliveryBuilder.AddDelivery<MyMessageConsumer, MyMessage>();
            });

            builder.WithPartitioningSupport((serviceProvider, partSettings) =>
            {
                // Example configuration for processing messages for each tenant
                partSettings.ForEachTenant = true; 
                partSettings.GetTenantIds = cancellationToken => Task.FromResult(new int[] { 1, 2 });
            });
        });

        // used PostgreSQL
        services.AddOutboxUsingPostgreSql(cfg =>
        {
            cfg.AddDataSource(c => c.WithConnectionString("Host=my_host;Database=my_db;Username=my_user;Password=my_password"));
            cfg.WithPgOutboxSettings((_, settings) =>
            {
                settings.TableSettings.DatabaseSchemaName = "public"; 
                settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(30); 
            });
        });
    }
}
```

### Message

```csharp
[OutboxMessage]
public record MyMessage(string PayloadId, string Content, int TenantId) : IOutboxPayloadMessage;
```

### Consume

```csharp
using Sa.Outbox;

namespace MyNamespace
{
    public class MyMessageConsumer : IConsumer<MyMessage>
    {
        public async ValueTask Consume(IReadOnlyCollection<IOutboxContext<MyMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            foreach (var messageContext in outboxMessages)
            {
                Console.WriteLine($"Processing message with ID: {messageContext.Payload.PayloadId} and Content: {messageContext.Payload.Content}");
                messageContext.Ok("Message processed successfully.");
            }
        }
    }
}
```

### Sending

```csharp
public class MessageSender(IOutboxMessagePublisher publisher)
{
    public async Task SendMessagesAsync(CancellationToken cancellationToken)
    {
        var messages = new List<MyMessage>
        {
            new MyMessage { PayloadId = Guid.NewGuid().ToString(), Content = "Hello, World!", TenantId = 1 },
            new MyMessage { PayloadId = Guid.NewGuid().ToString(), Content = "Another message", TenantId = 2 }
        };

        await publisher.Publish(messages, cancellationToken);
    }
}
```
