# Sa.Outbox.PostgreSql

Предназначен для реализации паттерна Outbox с использованием PostgreSQL, который используется для обеспечения надежной доставки сообщений в распределенных системах. Он помогает избежать потери сообщений и гарантирует, что сообщения будут обработаны даже в случае сбоев.

## Основные функции
- **Надежная доставка сообщений**: Обеспечивает сохранение сообщений в базе данных до их успешной обработки.
- **Поддержка транзакций**: Позволяет отправлять сообщения в рамках одной транзакции с изменениями в базе данных.
- **Гибкость**: Поддерживает различные типы сообщений и их обработчиков.
- **Параллельная обработка**: Позволяет обрабатывать сообщения параллельно, что увеличивает производительность системы.



## Примеры

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
            builder.WithDeliveries(deliveryBuilder =>
            {
                deliveryBuilder.AddDelivery<MyMessageConsumer, MyMessage>();
            });

            builder.WithPartitioningSupport((serviceProvider, partSettings) =>
            {
                // Пример настройки для обработки сообщений для каждого арендатора
                partSettings.ForEachTenant = true; 
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
            cfg.AddDataSource(c => c.WithConnectionString("Host=my_host;Database=my_db;Username=my_user;Password=my_password"));
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

### Пример потребителя сообщений

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


