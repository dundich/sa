# Sa.Outbox

Базовая инфраструктурная библиотека для реализации паттерна **Transactional Outbox** в распределённых .NET-системах. Гарантирует атомарную запись сообщений вместе с бизнес-операциями в рамках одной транзакции БД, надёжную доставку, повторы, блокировки, многопоточность и поддержку мультитенантности.

Определяет абстракции и базовую логику — конкретная работа с БД (PostgreSQL, SQL Server и т.д.) реализуется в пакетах-провайдерах (`Sa.Outbox.PostgreSql`, `Sa.Outbox.SqlServer` и др.).

---

## Быстрый старт

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
    // Регистрация провайдера (пример — PostgreSQL)
    .AddSaOutboxUsingPostgreSql(cfg => cfg
        .WithDataSource(ds => ds.WithConnectionString("Host=localhost;Database=outbox"))
    );
```

---

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
|------|---------|
| **Publication** | Сообщения записываются в таблицу outbox внутри транзакции бизнес-операции через `IOutboxBulkWriter.InsertBulk()` |
| **Delivery** | Фоновые задания (`Sa.Schedule`) захватывают заблокированные сообщения, вызывают консьюмеры, обновляют статус |

---

## Основные типы

| Тип | Назначение |
|-----|-----------|
| `IOutboxBuilder` | Fluent-билдер конфигурации |
| `IOutboxMessagePublisher` | Публикация сообщений в outbox |
| `IConsumer\<TMessage\>` | Интерфейс консьюмера сообщений |
| `IOutboxContextOperations\<TMessage\>` | Операции изменения статуса доставки (`Ok`, `Error`, `Warn`, `Postpone` и т.д.) |
| `OutboxConsumerSettings` | Immutable-снимок настроек группы консьюмера (интервал, батчи, параллелизм, повторы…) |
| `OutboxConsumerSettingsBuilder` | Fluent-билдер для создания и частичного обновления `OutboxConsumerSettings` |
| `IOutboxConsumerManager` | Runtime-менеджер: атомарный свап, пауза/возобновление, изменение подписок |
| `IDeliverySnapshot` | Read-only представление зарегистрированных доставок для диагностики |
| `DeliveryStatus` / `DeliveryStatusCode` | HTTP-подобные коды статуса доставки |
| `ExponentialBackoffRetryStrategy` | Экспоненциальный backoff с jitter |
| `OutboxPartInfo` | Информация о партиции: TenantId, PartName |

---

## Коды статуса доставки

Полный набор HTTP-подобных кодов статуса:

| Код | Статус | Значение |
|-----|--------|---------|
| 200 | `Ok()` | Успешно обработано |
| 201 | `Created()` | Создан ресурс побочного эффекта |
| 202 | `Accepted()` | Принято для асинхронной обработки |
| 203 | `Ok203()` | Non-Authoritative Information |
| 204 | `NoContent()` | Обработано, нет данных |
| 299 | `Aborted()` | Намеренно пропущено |
| 301 | `MovedPermanently()` | Перемещено в другую очередь |
| 400 | `Warn()` | Временная ошибка → повтор |
| 500–507 | `ErrorXXX()` | Постоянная ошибка |
| 508 | `ErrorMaxAttempts()` | Исчерпан макс. число попыток |
| 103 | `Postpone()` | Отложенная обработка |
| 104 | `Retry()` | Повторить сейчас |

---

## Методы статуса

После обработки каждого сообщения вызовите ровно один метод из `IOutboxContextOperations<T>`:

| Метод | Описание |
|-------|---------|
| `msg.Ok(message?)` | Успешно обработано (200 OK) |
| `msg.Created(message?)` | Создан ресурс побочного эффекта (201 Created) |
| `msg.Accepted(message?)` | Принято для асинхронной обработки (202 Accepted) |
| `msg.NoContent(message?)` | Обработано, нет данных (204 No Content) |
| `msg.Aborted(message?)` | Намеренно пропущено (299 Aborted) |
| `msg.Warn(exception, message?, postpone?)` | Временная ошибка → повтор (400 Warn) |
| `msg.Error(exception, message?)` | Постоянная ошибка (500 Error) |
| `msg.ErrorMaxAttempts()` | Исчерпан макс. число попыток (508) |
| `msg.Postpone(delay, message?)` | Отложить обработку (103 Postpone) |
| `msg.Retry(delay, message?)` | Повторить с метаданными (104 Retry) |

---

## Конфигурация

### Регистрация консьюмеров

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveries(d => d
        // Singleton delivery (один инстанс на всё приложение)
        .AddDelivery<MyConsumer, MyMessage>("orders", (sp, cs) => {
            cs
                .WithMaxBatchSize(32)
                .WithLockDuration(TimeSpan.FromSeconds(10))
                .WithMaxDeliveryAttempts(5)
                .WithInterval(TimeSpan.FromSeconds(30))
                .WithInitialDelay(TimeSpan.FromSeconds(5));
        })
        // Scoped delivery (DI-scoped на каждую доставку)
        .AddDeliveryScoped<TenantAwareConsumer, EventData>("events")
    )
);
```

> **Self-bootstrapping:** `DeliveryJob` автоматически регистрирует настройки в `IOutboxConsumerManager` при первом выполнении. Отдельный сервис инициализации не нужен — каждое задание читает живые снимки из менеджера, включая runtime-изменения через `Apply()`.

### Управление настройками во время выполнения

`IOutboxConsumerManager` позволяет менять настройки без перезапуска:

```csharp
// Атомарный свап — новый снимок применяется атомарно
manager.Apply("orders", s => s with { MaxBatchSize = 64 });

// Пауза / Возобновление
manager.Pause("orders");
manager.Resume("orders");

// Подписка на изменения
using var sub = manager.Subscribe("orders", updated =>
{
    // реакция на изменение настроек
});

// Проверка состояния
bool paused = manager.IsPaused("orders");
bool registered = manager.IsRegistered("orders");

// Удаление (удаляет настройки И.detach внешний контроль)
manager.Unregister("orders");

// Список всех групп
var allGroups = manager.GetAllConsumerGroupIds();
```

### Настройки потребления

`OutboxConsumerSettings` — единый immutable record. Все параметры задаются через `OutboxConsumerSettingsBuilder`:

| Параметр | По умолчанию | Описание |
|----------|-------------|---------|
| `ConsumerGroupId` | — | Уникальный идентификатор группы |
| `AsSingleton` | true | Singleton (один на кластер) vs Scoped |
| `Interval` | 1 мин | Период выполнения между итерациями |
| `InitialDelay` | 10 сек | Задержка перед первым выполнением |
| `ConcurrencyLimit` | 1 | Количество параллельных воркеров |
| `MaxConcurrency` | 48 | Абсолютный потолок процессоров |
| `IterationDelay` | 0 сек | Задержка между итерациями в цикле |
| `MaxProcessingIterations` | 10 | Итераций за цикл (-1 = бесконечно) |
| `LockDuration` | 10 сек | TTL блокировки записи |
| `LockRenewal` | 3 сек | Интервал продления блокировки |
| `LookbackInterval` | 7 дней | Окно поиска истории необработанных сообщений |
| `MaxDeliveryAttempts` | 3 | Макс. попыток доставки перед DLQ |
| `MaxBatchSize` | 16 | Макс. сообщений в батче |
| `BatchingWindow` | 3 сек | Окно агрегации сообщений |
| `PerTenantTimeout` | 0 | Таймаут на обработку одного арендатора |
| `PerTenantMaxDegreeOfParallelism` | 1 | Параллелизм по арендаторам (1 = последовательно, -1 = все ядра) |
| `RetryCountOnError` | 0 | Повторы при ошибке (-1 = бесконечно) |
| `Paused` | false | Флаг паузы |

---

### Мультитенантность

```csharp
.WithTenants((_, ts) => ts
    .WithTenantIds(1, 2, 3)                          // Явный список
    .WithAutoDetect()                                // Автодетект из сообщений в рантайме
    .WithTenantDetector<TenantDetector>()            // Кастомный детектор
    .WithTenantParallelProcessing(3)                 // Параллельная обработка по арендаторам
)
```

---

### Метаданные сообщений

```csharp
// Вариант 1: явный partName и резолвер PayloadId
options.AddMetadata<MyMessage>(partName: "orders", getPayloadId: m => m.Id);

// Вариант 2: вывод из IOutboxPublishable
options.AddMetadata<MyMessage>();
```

---

## Доступные провайдеры

| Провайдер | Пакет | Статус |
|-----------|-------|--------|
| PostgreSQL | `Sa.Outbox.PostgreSql` | ✅ production-ready |
| SQL Server | `Sa.Outbox.SqlServer` | 🔧 в разработке |
| Redis | `Sa.Outbox.Redis` | 🔧 в разработке |

---

## Требования к провайдерам

Провайдер должен реализовать три ключевых интерфейса:

| Интерфейс | Назначение |
|-----------|-----------|
| `IOutboxBulkWriter` | Массовая вставка сообщений в БД |
| `IOutboxDeliveryManager` | Блокировки и диспетчеризация сообщений |
| `ITenantSource` | Источник ID арендатора |

---

## Зависимости

- **Sa.Schedule** — планировщик фоновых заданий
- Базовые классы из **Sa** (LockRenewer, MurmurHash3, Retry, расширения)

---

## Лицензия

MIT
