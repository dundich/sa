# Sa.Outbox.PostgreSql

AOT Библиотека для реализации паттерна **Transactional Outbox** с использованием PostgreSQL в .NET приложениях.
Обеспечивает гарантированную доставку сообщений с поддержкой мультитенантности, конкурентной обработки и расширенным управлением консьюмерами.

## Быстрый старт

### Установка
```bash
dotnet add package Sa.Outbox.PostgreSql
```

### Конфигурация

```csharp
ConfigureServices(services => services
    .AddOutbox(builder => builder
        .WithTenantSettings((_, ts) => ts.WithTenantIds(1, 2, 3))
        .WithDeliveries(builder => builder
            .AddDelivery<MyConsumer, MyMessage>((_, settings) =>
            {
                settings.TableSettings.WithSchema("my_outbox");
                settings.ScheduleSettings.WithIntervalSeconds(5);
            })
        )
    )
)
```

### Публикация сообщений

```csharp
public sealed record MyMessage(string PayloadId, int TenantId = 0) : IOutboxPayloadMessage
{
    public static string PartName => "root";
}

// Пакетная публикация для разных tenant-ов
await publisher.Publish([
    new MyMessage("#1", 1),
    new MyMessage("#2", 2),
    new MyMessage("#3", 3)
]);
```


### Обработка сообщений

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

## Сообщения

Все сообщения для публикации должны поддерживать интерфейс:

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
/// Represents a message payload in the Outbox system.
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


## Основные возможности

### 1. Мультиконсьюмерность
Один тип сообщения может обрабатываться несколькими независимыми консьюмерами.
Каждый консьюмер имеет собственную очередь с отслеживаемым смещением (offset).

### 1. Мультитенантности
Библиотека разработана с учетом изоляции данных между tenant-ами:

```csharp
.WithTenantSettings((_, ts) => ts
    .WithTenantIds(1, 2, 3)                     // Явное указание tenant-ов
    .WithAutoDetect()                           // Или автоматическое определение
    .WithTenantDetector<TenantDetector>()       // Кастомный детектор tenant-ов
    .WithTenantParallelProcessing(3)            // Могут обрабатываться одновременно
)
```

### 1. Массовые операции
- **Пакетная публикация**: для максимальной производительности
- **Пакетная обработка**: консьюмеры получают сообщения пачками
- **Пакетное подтверждение**: массовое обновление статусов

### 1. Расширенная система статусов
- **`Ok()`** - успешная обработка
- **`Error(Exception)`** - перманентная ошибка
- **`Warn(Exception)`** - временная ошибка с ретраем
- **`Postpone(TimeSpan)`** - отложить обработку
- **`Aborted(string)`** - пропустить с указанием причины

### 1. Pull-модель со статическим и динамическим управлением
```csharp
settings.ConsumeSettings
    .WithIntervalSeconds(5)
    .WithMaxDeliveryAttempts(3)
    .WithBatchingWindow(TimeSpan.FromMinutes(5))
    .WithLockDuration(TimeSpan.FromMinutes(10));

// Динамическое управление во время выполнения
    public async ValueTask Consume(ConsumerGroupSettings settings,...
        settings.ConsumeSettings.WithMaxProcessingIterations(100);
```

## Архитектура БД

### Структура таблиц

```
📂 outbox (schema)
├── 📄 outbox__msg$     - Исходные сообщения (read-only)
├── 📄 outbox           - Очереди задач для консьюмеров (read-write)
├── 📄 outbox__log$     - История доставки (read-only)
├── 📄 outbox__error$   - Перманентные ошибки (read-only)
├── 📄 outbox__type$    - Регистрация типов сообщений (read-only)
└── 📄 outbox__offset$  - Смещения для консьюмер-групп (read-write)
```

### Особенности реализации

- **BINARY COPY** для массовой вставки сообщений `outbox__msg$`
- **SKIP LOCKED** для конкурентной обработки `outbox`
- **Advisory Locks** для координации смещений `outbox__offset$`
- **Batch-операции** для минимизации round-trip

### Настройки таблиц
```csharp
.WithOutboxSettings((_, settings) =>
{
    // Схема и таблицы
    settings.TableSettings
        .WithSchema("my_schema")
        // поля таблицы
        .TaskQueue
           .Fields = {
                TaskId = "id",
                TenantId = "client_id"
            };

    // Автоматическая миграция для партиций
    settings.MigrationSettings
        .WithForwardDays(2)
        .WithExecutionInterval(TimeSpan.FromHours(6));

    // Автоматическая очистка партиций
    settings.CleanupSettings
        .WithDropPartsAfterRetention(TimeSpan.FromDays(30));
})
```





## Требования

- **.NET 9.0** или выше
- **PostgreSQL 15+**

## Лицензия

MIT