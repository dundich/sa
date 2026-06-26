# Sa.Outbox

Базовая инфраструктурная библиотека для реализации паттерна **Transactional Outbox** в распределённых .NET-системах. Гарантирует атомарную запись сообщения вместе с бизнес-операцией внутри одной транзакции БД и надёжную доставку с поддержкой повторных попыток, блокировок, многопоточности и мультитенантности.

Библиотека определяет абстракции и логику — конкретную работу с БД (PostgreSQL, SQL Server и т.д.) реализуют провайдеры-наследники (`Sa.Outbox.PostgreSql`, `Sa.Outbox.SqlServer` и др.).

## Quick Start

### 1. Установите пакет провайдера

```bash
dotnet add package Sa.Outbox.PostgreSql
```

### 2. Настройте DI

```csharp
builder.Services
    .AddSaOutbox(builder => builder
        .WithTenants((_, ts) => ts.WithTenantIds(1, 2, 3))
        .WithDeliveries(b => b.AddDelivery<MyConsumer, MyMessage>())
    )
    // провайдер (пример — PostgreSQL)
    .AddSaOutboxUsingPostgreSql(cfg => cfg
        .WithDataSource(ds => ds.WithConnectionString("Host=localhost;Database=outbox"))
    );
```

### 3. Публикация сообщений

```csharp
public sealed record OrderCreated(string OrderId);

await publisher.Publish(
    [new OrderCreated("ORD-001"), new OrderCreated("ORD-002")],
    tenantId: 1);
```

### 4. Потребление сообщений

```csharp
sealed class OrderCreatedConsumer : IConsumer<OrderCreated>
{
    public async ValueTask Consume(
        ConsumerGroupSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<OrderCreated>> messages,
        CancellationToken cancellationToken)
    {
        foreach (var msg in messages.Span)
        {
            // обработка...
            msg.Ok($"Processed order {msg.Payload.OrderId}");
        }
    }
}
```

## Архитектура

```
┌──────────────┐     Publish      ┌─────────────┐
│  Application │ ───────────────► │ outbox__msg$│
│              │                  │   (source)   │
│  IConsumer   │                  └──────┬──────┘
│              │                         │ RentDelivery (SKIP LOCKED)
│              │                         ▼
└──────────────┘               ┌─────────────┐
      ▲                        │    outbox   │
      └── Ack/Warn/Error ◄─────┤  (queue)    │
                               └─────────────┘
```

### Два этапа жизненного цикла

| Этап | Описание |
|------|----------|
| **Publication** | Сообщения записываются в таблицу outbox внутри транзакции бизнес-операции через `IOutboxBulkWriter.InsertBulk()` |
| **Delivery** | Фоновые задачи (`Sa.Schedule`) захватывают заблокированные сообщения, вызывают потребителей, обновляют статус |

## Основные типы

| Тип | Назначение |
|-----|------------|
| `IOutboxBuilder` | Fluent-билдер для конфигурации outbox-системы |
| `IOutboxMessagePublisher` | Публикация сообщений в outbox |
| `IConsumer\<TMessage\>` | Интерфейс потребителя сообщений |
| `IOutboxContextOperations\<TMessage\>` | Операции изменения статуса доставки |
| `ConsumeSettings` | Настройки потребления (батчи, блокировки, повторы) |
| `ConsumerGroupSettings` | Группа потребителей + расписание |
| `DeliveryStatus` / `DeliveryStatusCode` | HTTP-подобные статусы доставки |
| `ExponentialBackoffRetryStrategy` | Экспоненциальный бэкофф с джиттером |
| `OutboxPartInfo` | Информация о части: TenantId, PartName |

## Статусы доставки

Полный набор HTTP-подобных кодов состояния:

| Код | Статус | Значение |
|-----|--------|----------|
| 200 | `Ok()` | Успешно обработано |
| 201 | `Created()` | Создан побочный ресурс |
| 202 | `Accepted()` | Принято в обработку |
| 204 | `NoContent()` | Обработано, нет данных |
| 299 | `Aborted()` | Пропущено |
| 400 | `Warn()` | Временная ошибка → повтор |
| 500–508 | `Error()` | Постоянная ошибка |
| 508 | `ErrorMaxAttempts()` | Исчерпан максимум попыток |
| 103 | `Postpone()` | Отложенная обработка |
| 104 | `Retry()` | Повторить сейчас |

## Конфигурация

### Настройка потребителей

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveries(d => d
        // Singleton delivery (один экземпляр на всё приложение)
        .AddDelivery<MyConsumer, MyMessage>("orders", (sp, cs) => {
            cs.ConsumeSettings
                .WithMaxBatchSize(32)
                .WithLockDuration(TimeSpan.FromSeconds(10))
                .WithMaxDeliveryAttempts(5);
            cs.ScheduleSettings
                .WithInterval(TimeSpan.FromSeconds(30))
                .WithInitialDelay(TimeSpan.FromSeconds(5));
        })
        // Scoped delivery (DI-скон на каждую доставку)
        .AddDeliveryScoped<TenantAwareConsumer, EventData>("events")
    )
);
```

### Настройки потребления

| Параметр | По умолчанию | Описание |
|----------|-------------|----------|
| `MaxBatchSize` | 16 | Макс. размер батча |
| `LockDuration` | 10 сек | Время блокировки сообщения |
| `LockRenewal` | 3 сек | Период продления блокировки |
| `MaxDeliveryAttempts` | 3 | Максимум попыток доставки |
| `LookbackInterval` | 7 дней | История обработки |
| `ConcurrencyLimit` | 1 | Одновременных задач |
| `PerTenantMaxDegreeOfParallelism` | 1 | Параллельность по тенантам |

### Мультитенантность

```csharp
.WithTenants((_, ts) => ts
    .WithTenantIds(1, 2, 3)                          // Явный список
    .WithAutoDetect()                                // Автоопределение из БД
    .WithTenantDetector<TenantDetector>()            // Кастомный детектор
    .WithTenantParallelProcessing(3)                 // Параллельная обработка
)
```

### Метаданные сообщений

```csharp
// Вариант 1: явное указание partName и PayloadId
options.AddMetadata<MyMessage>(partName: "orders", getPayloadId: m => m.Id);

// Вариант 2: из IOutboxPublishable
options.AddMetadata<MyMessage>();
```

## Доступные провайдеры

| Провайдер | Пакет | Статус |
|-----------|-------|--------|
| PostgreSQL | `Sa.Outbox.PostgreSql` | ✅ production-ready |
| SQL Server | `Sa.Outbox.SqlServer` | 🔧 в разработке |
| Redis | `Sa.Outbox.Redis` | 🔧 в разработке |

## Требования к провайдеру

Провайдер должен реализовать три ключевых интерфейса:

| Интерфейс | Назначение |
|-----------|------------|
| `IOutboxBulkWriter` | Массовая вставка сообщений в БД |
| `IOutboxDeliveryManager` | Управление блокировкой и выдачей сообщений |
| `ITenantSource` | Источник идентификаторов тенантов |

## Зависимости

- **Sa.Schedule** — планировщик фоновых задач
- Ссылочные классы из **Sa** (LockRenewer, MurmurHash3, Retry, расширения)

## License

MIT
