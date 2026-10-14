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
        .WithDataSource(ds => ds.Options(ob => ob.Configure(o => o.ConnectionString = "Host=localhost;Database=outbox")))
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

> **Изоляция.** Задания доставки работают в **собственном** именованном расписании `Sa.Schedule`
> под ключом скопа `OutboxScope.DefaultKey` (`"outbox"`), поэтому они никогда не делят расписание с
> `AddSaSchedule()` приложения. Интеграция с PostgreSQL регистрирует свой модуль
> `Sa.Partitional.PostgreSql` под тем же ключом. Оба метода — `AddSaOutbox` и
> `AddSaOutboxUsingPostgreSql` — принимают необязательный последний аргумент `scopeKey`; передайте
> одно и то же значение в оба, чтобы перенести Outbox в другой скоп. Зарезервированный ключ
> `"default"` отклоняется.

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
| `IdleMaxInterval` | 0 (выключено) | Верхняя граница адаптивного backoff-а пустой очереди; при установке должен быть `>= Interval` |

### Политика ожидания пустой очереди

Backoff пустой очереди включается на каждую группу отдельно. Задайте базовый `Interval` (пока идёт работа) и предел `IdleMaxInterval` (пока пусто):

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveries(d => d
        .AddDelivery<OrderConsumer, OrderPlaced>("orders", (_, cs) => cs
            .WithInterval(TimeSpan.FromSeconds(5))      // 5 с, пока идёт работа
            .WithIdleBackoff(TimeSpan.FromMinutes(2))   // рост до 2 мин, пока пусто
        )
        .AddDelivery<AuditConsumer, AuditEvent>("audit", (_, cs) => cs
            .WithInterval(TimeSpan.FromSeconds(1))
            .WithFixedInterval()                        // оставить группу отзывчивой
        )
    ));
```

DI-значение по умолчанию — `ExponentialIdleWaitStrategy` (x2 за пустой опрос, джиттер 20%); он неактивен, пока группа не вызовет `WithIdleBackoff`. Переопределить алгоритм глобально можно по типу или экземпляром:

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveryBatcher<MyBatcher>()                                   // свой расчёт размера батча
    .WithIdleWaitStrategy(new ExponentialIdleWaitStrategy(1.5, 0.3))   // свои multiplier / jitter
    // или: .WithIdleWaitStrategy<MyIdleStrategy>()                     // полностью своя IEmptyQueueWaitStrategy
);
```

Пример собственной реализации `IEmptyQueueWaitStrategy` — в `Delivery/Readme-ru.md` → *Настройка политики ожидания*.

---

### Глобальные переопределения DI

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveryBatcher<MyBatcher>()          // свой расчёт размера батча
    .WithIdleWaitStrategy<MyIdleStrategy>()    // своя стратегия ожидания пустой очереди
);
```

Оба необязательны: `IDeliveryBatcher` по умолчанию — `DeliveryBatcher` (проброс `MaxBatchSize`), `IEmptyQueueWaitStrategy` — `ExponentialIdleWaitStrategy`. Стратегия ожидания по умолчанию неактивна, пока группа не включит её через `WithIdleBackoff(maxInterval)`, поэтому «из коробки» опрос идёт с фиксированным `Interval`.

---

### Мультитенантность

Арендатор задаётся выбором **одного** из режимов ниже — они взаимоисключающие,
поэтому в одной регистрации используется лишь одна цепочка.

```csharp
// Режим 1: фиксированный список ID арендаторов
.WithTenants((_, ts) => ts.WithTenantIds(1, 2, 3))
```

```csharp
// Режим 2: автодетект арендаторов из сообщений в рантайме
.WithTenants((_, ts) => ts
    .WithAutoDetect()                                // Автодетект из сообщений
    .WithTenantDetector<TenantDetector>()            // Кастомный детектор
    .WithTenantParallelProcessing(3)                 // Параллельная обработка по арендаторам
)
```

> **Примечание:** `WithTenantIds`, `WithTenantSource` и `WithTenantProvider` все
> помечают источник арендаторов как авторитетный, отключая автодетект внутри
> (эквивалент добавления `.WithNoAutoDetect()`). Любой из них можно сочетать с
> `WithTenantParallelProcessing`, но никогда — с цепочкой автодетекта выше. Как только
> любой из них вызван, ветка автодетекта через plug-сервис пропускается и
> переданный список/источник/провайдер используется без изменений.
>
> Два режима взаимоисключающие и проверяются на этапе конфигурации: вызов
> `WithAutoDetect()` после любого из них бросит <c>InvalidOperationException</c> вместо
> тихого игнорирования явного источника. Выберите один режим на регистрацию.

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
