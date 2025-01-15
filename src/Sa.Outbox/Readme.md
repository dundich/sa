# Outbox

Базовая локика + абстракции предназначеные для реализации паттерна Outbox, с поддержкой партиционирования. 

## Базовый интерфейс для определения Outbox сообщения

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

[OutboxMessage(part:"some_part")]  // задаем партицию
public record SomeMessage(long Payload) : IOutboxPayloadMessage
{
    public string PayloadId => String.Empty;
    public int TenantId => 0;
}
```



## Основные интерфейсы по работе с сообщениями

### Публикация сообщения

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

### Сохранение в хранилище

```csharp
public interface IOutboxRepository
{
    ValueTask<ulong> Save<TMessage>(string payloadType, ReadOnlyMemory<OutboxMessage<TMessage>> messages, CancellationToken cancellationToken = default);
}
```


### Доставка до потребителя

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


### Поддержка партиций 
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

### Потребитель сообщений
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



## Примеры Outbox с использованием PostgreSQL

### Пример конфигурирования 

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.PostgreSql;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Конфигурация Outbox
        services.AddOutbox(builder =>
        {
            // Настройка доставки 
            builder.WithDeliveries(deliveryBuilder =>
            {
                // Добавить доставку (подкл. потребителя сообщений с типом - MyMessage)
                deliveryBuilder.AddDelivery<MyMessageConsumer, MyMessage>();
            });

            // Настройка поддержки для работы с партициями
            builder.WithPartitioningSupport((serviceProvider, partSettings) =>
            {
                // Пример настройки для обработки сообщений для каждого арендатора
                partSettings.ForEachTenant = true; 

                // Возвращаем список тенантов для app
                partSettings.GetTenantIds = async cancellationToken =>
                {
                    // Логика получения идентификаторов арендаторов
                    return await Task.FromResult(new int[] { 1, 2 });
                };
            });
        });

        // Подключение Outbox с использованием PostgreSQL
        services.AddOutboxUsingPostgreSql(cfg =>
        {
            // коннекшен к БД 
            cfg.AddDataSource(c => c.WithConnectionString("Host=my_host;Database=my_db;Username=my_user;Password=my_password"));
            
            // настройки для работы Pg
            cfg.WithPgOutboxSettings((_, settings) =>
            {
                // Установка схемы базы данных
                settings.TableSettings.DatabaseSchemaName = "public";

                // Настройка очистки
                settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(30); 
            });
        });
    }
}
```

### Пример отправки сообщения

```csharp

public class MessageSender(IOutboxMessagePublisher publisher)
{
    public async Task SendMessagesAsync(CancellationToken cancellationToken)
    {
        // Создание списка сообщений для отправки
        var messages = new List<MyMessage>
        {
            new MyMessage { PayloadId = Guid.NewGuid().ToString(), Content = "Hello, World!", TenantId = 1 },
            new MyMessage { PayloadId = Guid.NewGuid().ToString(), Content = "Another message", TenantId = 2 }
        };

        // Отправка сообщений через Outbox
        ulong result = await publisher.Publish(messages, cancellationToken);

        Console.WriteLine($"Sent {result} messages.");
    }
}
```


### Пример потребления сообщений

```csharp
using Sa.Outbox;

namespace MyNamespace
{
    // Пример сообщения, которое будет отправляться через Outbox
    [OutboxMessage]
    public record MyMessage(string PayloadId, string Content) : IOutboxPayloadMessage
    {
        public int TenantId { get; init; } // Идентификатор арендатора
    }

    // Пример потребителя, который будет обрабатывать сообщения MyMessage
    public class MyMessageConsumer : IConsumer<MyMessage>
    {
        public async ValueTask Consume(IReadOnlyCollection<IOutboxContext<MyMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            foreach (var messageContext in outboxMessages)
            {
                // Логика обработки сообщения
                Console.WriteLine($"Processing message with ID: {messageContext.Payload.PayloadId} and Content: {messageContext.Payload.Content}");

                // Успешная обработка сообщения
                messageContext.Ok("Message processed successfully.");
            }
        }
    }
}
```

