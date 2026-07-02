# Sa.Outbox.PostgreSql

AOT-библиотека для реализации паттерна **Transactional Outbox** с использованием PostgreSQL в .NET приложениях.
Обеспечивает гарантированную доставку сообщений с поддержкой мультитенантности, конкурентной обработки и расширенным управлением консьюмерами.

---

## Быстрый старт (5 минут)

Полный рабочий пример — скопируйте, вставьте, запустите:

```csharp
using Microsoft.Extensions.Hosting;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Publication;

IHost host = Host.CreateDefaultBuilder()
    .ConfigureServices(services => services
        // ① Регистрация ядра outbox + тенантов + консьюмеров
        .AddSaOutbox(builder => builder
            .WithTenants((_, t) => t.WithTenantIds(1, 2, 3))
            .WithDeliveries(b => b
                .AddDeliveryScoped<OrderConsumer, OrderCreated>((_, s) =>
                {
                    s
                        .WithInterval(TimeSpan.FromSeconds(5))
                        .StartImmediately()
                        .WithMaxBatchSize(16)
                        .WithMaxDeliveryAttempts(3)
                        .WithLockDuration(TimeSpan.FromSeconds(10))
                        .WithLookbackInterval(TimeSpan.FromDays(7));
                })
            )
        )
        // ② Подключение к PostgreSQL
        .AddSaOutboxUsingPostgreSql(cfg => cfg
            .WithDataSource(ds => ds
                .WithConnectionString("Host=localhost;Database=outbox_db;Username=postgres;Password=postgres"))
            .WithOutboxSettings((_, settings) =>
            {
                settings.TableSettings.WithSchema("outbox");
                settings.CleanupSettings.WithDropPartsAfterRetention(TimeSpan.FromDays(30));
            })
            .WithMessageSerializer(OutboxMessageSerializer.Instance)
        )
    )
.Build();

// ③ Публикация сообщения
var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();
await publisher.Publish([
    new OrderCreated("order-42", "Премиум виджет"),
    new OrderCreated("order-43", "Стандартный гаджет")
], tenantId: 1);

// ④ Запуск (консьюмеры автоматически подхватят сообщения)
await host.RunAsync();


// ─── Ваши типы ────────────────────────────────────────────────

public sealed record OrderCreated(string PayloadId, string ProductName);

public sealed class OrderConsumer : IConsumer<OrderCreated>
{
    public async ValueTask Consume(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<OrderCreated>> messages,
        CancellationToken ct)
    {
        foreach (var msg in messages.Span)
        {
            Console.WriteLine($"[{filter.ConsumerGroupId}] {msg.Payload.ProductName}");
            msg.Ok();   // ← отметить успех
        }
    }
}
```

Всё остальное библиотека берёт на себя:
- Создание таблиц и партиционирование
- Массовую вставку через BINARY COPY
- Конкурентную обработку через `SKIP LOCKED`
- Повтор / отложенный повтор / фиксацию ошибок
- Автоматическую очистку старых партиций
- Self-bootstrapping настроек консьюмера в `IOutboxConsumerManager`

---

## Руководство по настройке

Вся конфигурация разбивается на два вызова регистрации:

| Вызов | Назначение |
|---|---|
| `AddSaOutbox(...)` | Ядро: тенанты, консьюмеры, расписание |
| `AddSaOutboxUsingPostgreSql(...)` | PostgreSQL: подключение, таблицы, миграция, очистка |

### 1. Настройка тенантов

Внутри `AddSaOutbox`:

```csharp
.AddSaOutbox(builder => builder
    .WithTenants((_, ts) => ts
        .WithTenantIds(1, 2, 3)                         // Фиксированный список
        // .WithAutoDetect()                             // Автоопределение из сообщений
        // .WithTenantDetector<MyDetector>()             // Кастомный детектор
        // .WithTenantParallelProcessing(4)              // Макс воркеров на тенант
    )
    // ...
)
```

### 2. Регистрация консьюмеров

Внутри `AddSaOutbox`:

```csharp
.AddSaOutbox(builder => builder
    .WithDeliveries(b => b
        // Простейший вариант — имя consumer group из типа
        .AddDeliveryScoped<GreetingConsumer, EmailSent>()

        // Кастомное имя группы (несколько консьюмеров на один тип)
        .AddDeliveryScoped<EmailAnalyticsConsumer, EmailSent>("analytics")

        // С настройками
        .AddDeliveryScoped<OrderConsumer, OrderCreated>((_, settings) =>
        {
            settings
                .WithInterval(TimeSpan.FromSeconds(5))
                .StartImmediately()                     // стартовать сразу
                .WithMaxBatchSize(16)                   // макс сообщений в батче
                .WithMaxDeliveryAttempts(3)             // стоп ретраев после N попыток
                .WithLockDuration(TimeSpan.FromSeconds(10))
                .WithLookbackInterval(TimeSpan.FromDays(7));
        })
    )
    // ...
)
```

#### Справочник настроек `OutboxConsumerSettingsBuilder`

Единый fluent-билдер для всех параметров consumer group:

| Метод | По умолчанию | Описание |
|---|---|---|
| `WithInterval(span)` | 1 мин | Период опроса |
| `StartImmediately()` | — | Старт без ожидания первого интервала |
| `WithMaxBatchSize(n)` | 16 | Макс. сообщений за батч |
| `WithMaxDeliveryAttempts(n)` | 3 | Стоп-повторы после N попыток |
| `WithLockDuration(span)` | 10 с | TTL блокировки сообщения |
| `WithLockRenewal(span)` | 3 с | Период продления блокировки |
| `WithLookbackInterval(span)` | 7 дн | История поиска необработанных |
| `WithBatchingWindow(span)` | 3 с | Окно агрегации сообщений |
| `WithNoBatchingWindow()` | — | Взять всё доступное сейчас |
| `WithConcurrencyLimit(n)` | 1 | Одновременных задач |
| `WithMaxConcurrency(n)` | 48 | Макс. параллельных процессоров |
| `WithRetryCountOnError(n)` | 0 | Повторы при ошибке (-1 = бесконечно) |
| `WithNoRetries()` | — | Отключить повторы |
| `WithInfiniteRetries()` | — | Бесконечные повторы |
| `WithMaxProcessingIterations(n)` | 10 | Итераций за цикл (-1 = безлимитно) |
| `WithSingleIteration()` | — | Одна итерация (тестирование) |
| `WithUnlimitedIterations()` | — | Безлимитные итерации |
| `WithIterationDelay(span)` | 0 с | Задержка между итерациями |
| `WithPerTenantTimeout(span)` | 0 | Таймаут обработки одного тенанта |
| `WithPerTenantMaxDegreeOfParallelism(n)` | 1 | Параллельность по тенантам (1 = последовательно, -1 = все ядра) |
| `WithSequentialProcessing()` | 1 | Последовательная обработка по тенантам |
| `WithMaxParallelism()` | -1 | Максимальная параллельность по тенантам |
| `Paused(bool)` | false | Пауза consumer group |
| `Resumed()` | — | Снять с паузы |
| `AsSingleton(bool)` | true | Singleton (один на кластер) vs Scoped |

#### Runtime-управление настройками

Настройки автоматически регистрируются в `IOutboxConsumerManager` при первом запуске job'а. Для runtime-изменений:

```csharp
var manager = host.Services.GetRequiredService<IOutboxConsumerManager>();

// Atomic swap — новый снимок применяется на следующей итерации
manager.Apply("cg_order_consumer", s => s with { MaxBatchSize = 64 });

// Pause / Resume
manager.Pause("cg_order_consumer");
manager.Resume("cg_order_consumer");

// Подписка на изменения
using var sub = manager.Subscribe("cg_order_consumer", updated =>
{
    // реакция на изменение настроек
});

// Проверка состояния
bool paused = manager.IsPaused("cg_order_consumer");
```

> **Self-bootstrapping:** `DeliveryJob` автоматически регистрирует настройки в `IOutboxConsumerManager` при первом запуске. Используется `TryRegister` для безопасной конкурентной регистрации — если несколько инстансов приложения стартуют одновременно, только один успешно зарегистрирует настройки, остальные прочитают канонический снимок из менеджера.

### 3. Подключение к PostgreSQL

Внутри `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds
        .WithConnectionString("Host=...;Database=...;Username=...;Password=...")
        // .WithMinimumPoolSize(5)
        // .WithMaximumPoolSize(100)
    )
    // ...
)
```

### 4. Сериализация сообщений

Внутри `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithMessageSerializer(OutboxMessageSerializer.Instance)   // синглтон-экземпляр
    // .WithMessageSerializer<MySerializer>()                   // DI-resolved transient
    // .WithMessageSerializer(sp => sp.GetRequiredService<MySerializer>())  // фабрика
)
```

#### AOT-совместимый сериализатор

Для Native AHT избегайте рефлексии:

```csharp
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(OrderCreated))]
public partial class OrderJsonContext : JsonSerializerContext { }

public class OrderSerializer : IOutboxMessageSerializer
{
    public T? Deserialize<T>(Stream stream) => typeof(T) switch
    {
        Type t when t == typeof(OrderCreated) =>
            (T?)(object?)JsonSerializer.Deserialize(stream, OrderJsonContext.Default.OrderCreated),
        _ => default
    };

    public void Serialize<T>(Stream stream, T value)
    {
        if (typeof(T) == typeof(OrderCreated))
            JsonSerializer.Serialize(stream, value!, OrderJsonContext.Default.OrderCreated);
    }
}
```

### 5. Настройки таблиц

Внутри `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithOutboxSettings((_, settings) =>
    {
        // ── Схема ─────────────────────────────────────────
        settings.TableSettings.WithSchema("my_outbox");

        // ── Базовое имя таблицы (все выводятся от него) ──
        settings.TableSettings.UseBaseTableName("outbox");
        // Итог: outbox, outbox__msg$, outbox__log$, outbox__error$ и т.д.

        // ── FillFactor для каждой таблицы ─────────────────
        settings.TableSettings.Message.FillFactor = 100;       // только вставка
        settings.TableSettings.TaskQueue.FillFactor = 65;      // чтение+запись

        // ── Кастомные имена полей ─────────────────────────
        settings.TableSettings.TaskQueue.Fields =
        {
            TaskId = "id",
            TenantId = "client_id",
            ConsumerGroup = "grp"
        };

        // ── Индивидуальные имена таблиц ───────────────────
        settings.TableSettings.UseBaseTableName("events");
        // или по отдельности:
        settings.TableSettings.WithMsgTableName("inbox_messages");
        settings.TableSettings.WithDeliveryTableName("inbox_log");

        // ── Миграция (создание партиций) ─────────────────
        settings.MigrationSettings.AsBackgroundJob = true;     // по умолчанию
        settings.MigrationSettings.ForwardDays = 2;            // создать N дней вперёд
        settings.MigrationSettings.ExecutionInterval = TimeSpan.FromHours(6);

        // ── Очистка (удаление старых партиций) ────────────
        settings.CleanupSettings.AsBackgroundJob = true;       // по умолчанию
        settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(30);
        settings.CleanupSettings.ExecutionInterval = TimeSpan.FromHours(4);

        // ── Минимальное смещение (без повторной обработки) ─
        // settings.ConsumeSettings.WithMinOffset<OrderConsumer>(DateTimeOffset.Now);
    })
)
```

#### Обзор таблиц

| Свойство | Имя по умолчанию | Роль | FillFactor |
|---|---|---|---|
| `Message` | `outbox__msg$` | Исходные сообщения (BINARY COPY) | 100 |
| `TaskQueue` | `outbox` | Активная очередь задач (SKIP LOCKED) | 65 |
| `Delivery` | `outbox__log$` | История доставки | 100 |
| `Error` | `outbox__error$` | Перманентные ошибки | 100 |
| `Type` | `outbox__type$` | Кэш тип ↔ хеш | 100 |
| `Offset` | `outbox__offset$` | Смещения консьюмер-групп | 100 |

#### Стратегия партиционирования

| Таблица | Ключ партиции | Сортировочный ключ |
|---|---|---|
| Message | `tenant_id` + `msg_part` | `msg_created_at` |
| TaskQueue | `tenant_id` + `consumer_group` | `task_created_at` |
| Delivery | `tenant_id` + `consumer_group` | `delivery_created_at` |
| Error | только дата | `error_created_at` |
| Type / Offset | нет | — |

### 6. Управление задачами вручную

По умолчанию миграция и очистка работают как фоновые задания через `Sa.Schedule`. Можно управлять вручную:

```csharp
var migrationService = host.Services.GetRequiredService<IMigrationService>();
bool ok = await migrationService.WaitMigration(TimeSpan.FromSeconds(30), ct);

// Или проверить состояние
if (!migrationService.OnMigrated.IsCancellationRequested)
{
    // DeliveryJob заблокирован во время активной миграции
}
```

---

## Жизненный цикл сообщения

```
┌──────────────┐   BINARY COPY    ┌──────────────┐
│  Ваш код     │ ───────────────→ │ outbox__msg$ │
│  Publish()   │                  └──────┬───────┘
└──────────────┘                         │ INSERT INTO outbox (SKIP LOCKED)
                                         ↓
                                    ┌──────────────┐
                                    │    outbox     │ ← ваш IConsumer<T>
                                    │  (очередь)    │    читает и обрабатывает
                                    └──────┬───────┘
                                           │
                              ┌────────────┼────────────┐
                              ↓            ↓            ↓
                        ┌──────────┐ ┌──────────┐ ┌──────────┐
                        │ __log$   │ │ __error$ │ │ __offset │
                        │ история  │ │ перманент│ │ обновляется│
                        └──────────┘ └──────────┘ └──────────┘
```

---

## Статусы результата

После обработки каждого сообщения вызовите ровно один метод:

| Метод | Когда использовать | Что дальше |
|---|---|---|
| `msg.Ok()` | Всё прошло успешно | Задача удалена |
| `msg.Created()` | Создан побочный ресурс | Задача удалена |
| `msg.Accepted()` | Принято в асинхронную обработку | Задача удалена |
| `msg.NoContent()` | Обработано, данных нет | Задача удалена |
| `msg.Error(ex)` | Неустранимая ошибка | Запись в `__error$`, без повтора |
| `msg.Warn(ex)` | Временная проблема (сеть, таймаут) | Повтор при следующем опросе |
| `msg.Postpone(ts)` | Нужно подождать перед повтором | Повтор после `ts` |
| `msg.Retry(ts, reason)` | Повтор с метаданными | Повтор с информацией о попытке |
| `msg.Aborted(reason)` | Намеренно пропустить | Отмечено как пропущенное, без повтора |
| `msg.ErrorMaxAttempts()` | Исчерпан максимум попыток | Запись в `__error$`, без повтора |

---

## Архитектура БД

```
📂 my_outbox (схема)
├── 📄 outbox__msg$      — Исходные сообщения (read-only, BINARY COPY)
├── 📄 outbox            — Очередь задач (read-write, SKIP LOCKED)
├── 📄 outbox__log$      — История доставки (read-only)
├── 📄 outbox__error$    — Перманентные ошибки (read-only)
├── 📄 outbox__type$     — Реестр тип ↔ хеш (read-only)
└── 📄 outbox__offset$   — Смещения групп (advisory lock)
```

### Под капотом

| Механизм | Что решает |
|---|---|
| **UUID v7** | Монотонно растущие ID из timestamp |
| **BINARY COPY** | Максимальная скорость массовой вставки |
| **SKIP LOCKED** | Безопасная конкуренция воркеров за задачи |
| **Advisory Locks** | Координация смещений на consumer group + tenant |
| **murmurHash3** | Компактная идентификация типов, кэшированная в `__type$` |
| **SqlCacheSplitter** | Дробит крупные UPDATE-запросы на батчи ≤512 элементов |

---

## Требования

- **.NET 10.0** или выше
- **PostgreSQL 15+**
- Совместимо с **Native AOT**

## Лицензия

MIT
