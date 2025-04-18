# Outbox

The base logic and abstractions designed for implementing the Outbox pattern, with support for partitioning.

## Base Interface for Defining Outbox Messages

```csharp
/// <summary>
/// Represents a message payload in the Outbox system.
/// </summary>
public interface IOutboxPayloadMessage
{
    /// <summary>
    /// Gets the unique identifier for the payload.
    /// </summary>
    string PayloadId { get; }

    /// <summary>
    /// Gets the identifier for the tenant associated with the payload.
    /// </summary>
    public int TenantId { get; }
}


// example message

[OutboxMessage(part:"some_part")]  // Specify partition
public record SomeMessage(long Payload) : IOutboxPayloadMessage
{
    public string PayloadId => String.Empty;
    public int TenantId => 0;
}
```



## Main Interfaces for Working with Messages

### Publishing Messages

```csharp
/// <summary>
/// Defines a contract for publishing outbox messages.
/// </summary>
public interface IOutboxMessagePublisher
{
    /// <summary>
    /// Publishes a collection of messages.
    /// </summary>
    ValueTask<ulong> Publish<TMessage>(IReadOnlyCollection<TMessage> messages, CancellationToken cancellationToken = default)
        where TMessage : IOutboxPayloadMessage;

    /// <summary>
    /// Publishes a single message.
    /// </summary>
    ValueTask<ulong> Publish<TMessage>(TMessage messages, CancellationToken cancellationToken = default)
        where TMessage : IOutboxPayloadMessage => Publish(new[] { messages }, cancellationToken);
}
```

### Saving to Storage

```csharp
public interface IOutboxRepository
{
    ValueTask<ulong> Save<TMessage>(string payloadType, ReadOnlyMemory<OutboxMessage<TMessage>> messages, CancellationToken cancellationToken = default);
}
```


### Delivery to Consumer

```csharp
public interface IDeliveryRepository
{
    /// <summary>
    /// Exclusively take for processing for the client
    /// </summary>
    Task<int> StartDelivery<TMessage>(Memory<OutboxDeliveryMessage<TMessage>> writeBuffer, int batchSize, TimeSpan lockDuration, OutboxMessageFilter filter, CancellationToken cancellationToken);
    
    /// <summary>
    /// Complete the delivery
    /// </summary>
    Task<int> FinishDelivery<TMessage>(IOutboxContext<TMessage>[] outboxMessages, OutboxMessageFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Extend the delivery (retain the lock for the client)
    /// </summary>
    Task<int> ExtendDelivery(TimeSpan lockExpiration, OutboxMessageFilter filter, CancellationToken cancellationToken);
}
```


### Support for Partitions
```csharp
/// <summary>
/// Represents a pair of tenant identifier and part information in the Outbox system.
/// This record is used to associate a tenant with a specific part of the Outbox message.
/// </summary>
/// <param name="TenantId">The unique identifier for the tenant.</param>
/// <param name="Part">The part identifier associated with the tenant.</param>
public record struct OutboxTenantPartPair(int TenantId, string Part);

/// <summary>
/// Represents an interface for supporting partitioning in the Outbox processing system.
/// This interface defines a method for retrieving tenant-part pairs.
/// </summary>
public interface IOutboxPartitionalSupport
{
    /// <summary>
    /// Asynchronously retrieves a collection of tenant-part pairs.
    /// This method can be used to get the current mapping of tenants to their respective parts.
    /// </summary>
    Task<IReadOnlyCollection<OutboxTenantPartPair>> GetPartValues(CancellationToken cancellationToken);
}
```

### Message Consumer
```csharp
/// <summary>
/// Represents a consumer interface for processing Outbox messages of a specific type.
/// </summary>
public interface IConsumer<TMessage> : IConsumer
{
    /// <summary>
    /// Consumes a collection of Outbox messages.
    /// </summary>
    ValueTask Consume(IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages, CancellationToken cancellationToken);
}

/// <summary>
/// Represents a base consumer interface for processing Outbox messages.
/// This interface can be extended by specific consumer implementations.
/// </summary>
public interface IConsumer
{
}
```



## Examples of Outbox Using PostgreSQL

### Configuration

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.PostgreSql;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Outbox configuration
        services.AddOutbox(builder =>
        {
            // Delivery configuration
            builder.WithDeliveries(deliveryBuilder =>
            {
                // Add delivery (connect consumer for messages of type - MyMessage)
                deliveryBuilder.AddDelivery<MyMessageConsumer, MyMessage>();
            });

            // Configuration for partitioning support
            builder.WithPartitioningSupport((serviceProvider, partSettings) =>
            {
                // Example configuration for processing messages for each tenant
                partSettings.ForEachTenant = true; 

                // Return the list of tenants for the app
                partSettings.GetTenantIds = async cancellationToken =>
                {
                    // Logic to retrieve tenant identifiers
                    return await Task.FromResult(new int[] { 1, 2 });
                };
            });
        });

        // Connecting Outbox using PostgreSQL
        services.AddOutboxUsingPostgreSql(cfg =>
        {
            // Database connection 
            cfg.AddDataSource(c => c.WithConnectionString("Host=my_host;Database=my_db;Username=my_user;Password=my_password"));
            
            // Settings for working with Pg
            cfg.WithPgOutboxSettings((_, settings) =>
            {
                // Set the database schema
                settings.TableSettings.DatabaseSchemaName = "public";

                // Cleanup settings
                settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(30); 
            });
        });
    }
}
```

### Example of Sending a Message

```csharp

public class MessageSender(IOutboxMessagePublisher publisher)
{
    public async Task SendMessagesAsync(CancellationToken cancellationToken)
    {
        var messages = [
            new MyMessage { PayloadId = Guid.NewGuid().ToString(), Content = "Hello, World!", TenantId = 1 },
            new MyMessage { PayloadId = Guid.NewGuid().ToString(), Content = "Another message", TenantId = 2 }
        ];

        ulong result = await publisher.Publish(messages, cancellationToken);

        Console.WriteLine($"Sent {result} messages.");
    }
}
```


### Example of Consuming Messages

```csharp
using Sa.Outbox;

namespace MyNamespace
{

    [OutboxMessage]
    public record MyMessage(string PayloadId, string Content) : IOutboxPayloadMessage
    {
        public int TenantId { get; init; }
    }

    // Example consumer that will process MyMessage messages
    public class MyMessageConsumer : IConsumer<MyMessage>
    {
        public async ValueTask Consume(IReadOnlyCollection<IOutboxContext<MyMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            foreach (var messageContext in outboxMessages)
            {
                // Logic for processing the message
                Console.WriteLine($"Processing message with ID: {messageContext.Payload.PayloadId} and Content: {messageContext.Payload.Content}");

                // Successful message processing
                messageContext.Ok("Message processed successfully.");
            }
        }
    }
}
```

