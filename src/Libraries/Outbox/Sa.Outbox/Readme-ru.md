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
> `"default"` отклоняется. Consumer-группа, зарегистрированная под скопом без соответствующего
> провайдера, валит старт хоста с явной ошибкой, а не уходит молча в хранилище default-провайдера.

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
| `MaxBatchSize` | 128 | Макс. сообщений в батче |
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

### Настройки публикации

`WithPublishSettings` настраивает `OutboxPublishSettings` (сейчас — `MaxBatchSize`) для одного
скоупа outbox:

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithPublishSettings((_, settings) => settings.WithMaxBatchSize(1024)),
    scopeKey: "billing");
```

Запись неизменяемая, поэтому делегат получает текущий экземпляр и возвращает следующий. Таков
контракт: `Action<OutboxPublishSettings>` мог изменить только копию, которую никто не читал, из-за
чего каждый вызов был тихим no-op. **Миграция:** вызов в форме `Action` перепишите как
`(_, settings) => settings.WithMaxBatchSize(n)`; лямбда с телом должна возвращать настройки.

Делегат ключуется скоупом outbox, поэтому у каждого провайдера свои настройки публикации; default
провайдер дополнительно отдаёт их без ключа. Несколько вызовов для одного скоупа складываются в
порядке регистрации — каждый получает результат предыдущего, — а повторный `AddSaOutbox` для того же
скоупа заменяет ранее записанные колбэки, а не применяет их дважды.

---

### Сериализация

`IOutboxMessageSerializer` живёт в ядре (`Sa.Outbox.Serialization`) и является **единой**
стратегией сериализации: провайдер сохраняет ровно то, что она записала, и читает то, что она
произвела. Сериализатор регистрируется один раз и действует и на публикацию, и на доставку, для
любого провайдера.

По умолчанию ядро регистрирует `JsonOutboxMessageSerializer` (UTF-8 JSON, на рефлексии) как
fallback. Провайдер без собственного сериализатора использует его; заменить можно на уровне ядра —
одним вызовом для всех провайдеров — или через `WithMessageSerializer(...)` провайдера:

```csharp
builder.Services
    .AddSaOutbox(builder => builder
        .WithMessageSerializer<MyJsonSerializer>()   // либо инстанс, либо фабрика
    )
    .AddSaOutboxUsingPostgreSql(cfg => cfg.WithDataSource(...));
```

```csharp
// Тот же сервис ядра, но настроенный на провайдере:
builder.Services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithMessageSerializer(MyJsonSerializer.Instance));
```

`WithMessageSerializer(...)` удаляет прежние регистрации, поэтому побеждает последний вызов;
сериализатор, зарегистрированный в DI до `AddSaOutbox`, тоже перекрывает fallback. Интерфейсы
`Sa.Outbox.PostgreSql.Serialization.IOutboxMessageSerializer` и
`Sa.Outbox.Redis.Serialization.IOutboxMessageSerializer` оставлены только как совместимые подтипы
основного — в новом коде реализуйте интерфейс ядра.

`OutboxPayloadCodec` — парный мост в байты для провайдеров, которые хранят payload отдельным
массивом (stream-ы Redis): сериализует через пул-стрим и возвращает массив точного размера.

Для Native AOT подайте сериализатор на source-generated `JsonSerializerContext` (или другой
формат без рефлексии) и разверните его и на публикующей, и на потребляющей стороне до публикации
первого сообщения.

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

Поддерживаются payload-ы и `class`, и `struct`. Для value-типа зарегистрируйте метаданные
(`AddMetadata<T>()`, если он реализует `IOutboxPublishable`, или
`AddMetadata<T>(partName, getter)`), чтобы PayloadId резолвился через типизированный делегат.
Незарегистрированный struct, реализующий `IOutboxPublishable`, тоже работает, но fallback
боксит payload.

---

## Доступные провайдеры

| Провайдер | Пакет | Статус |
|-----------|-------|--------|
| PostgreSQL | [`Sa.Outbox.PostgreSql`](../Sa.Outbox.PostgreSql) | ✅ production-ready |
| Redis | [`Sa.Outbox.Redis`](../Sa.Outbox.Redis) | 🔧 в разработке |

Ядро не зависит от провайдера: один и тот же `AddSaOutbox`, регистрация тенантов/метаданных и
объявления consumer-ов работают поверх любого пакета. Различаются только хранилище и механика доставки.

### Что общего

- Один контракт: `IOutboxMessagePublisher`, `IConsumer<TMessage>`, `OutboxConsumerSettings`,
  `IOutboxContextOperations<TMessage>` и коды статусов.
- Порядок — в пределах `(tenant, part, тип payload)`, не глобальный.
- Доставка at-least-once; consumer-ы должны быть идемпотентны.
- Изоляция тенантов и окно lookback на группу.
- Identity payload — простое имя типа (`typeof(TMessage).Name`), поэтому два типа из разных
  namespace с одинаковым простым именем делят канал/stream.
- Ядро владеет батчингом, job-ом доставки, `LockRenewer`/`ExtendDelivery` и трактовкой статусов;
  провайдер реализует `IOutboxBulkWriter`, `IOutboxDeliveryManager` и `IOutboxTenantDetector`
  и регистрирует их через `AddOutboxProvider` с дескриптором (имя + возможности).
- Единая стратегия сериализации: `IOutboxMessageSerializer` и JSON-fallback живут в ядре, поэтому
  один и тот же сериализатор используют все провайдеры (см. *Сериализация* выше).
- У постоянных (permanent) исходов есть терминальный путь и подключаемый обработчик
  (`IPgOutboxPoisonHandler` / `IRedisOutboxPoisonHandler`).

### Кратко о различиях

| Аспект | `Sa.Outbox.PostgreSql` | `Sa.Outbox.Redis` |
|---|---|---|
| Хранилище | таблицы `msg$`, `task$`, `__log$`, `__error$`, `__offset$` | streams `stream:{tenant}:{part}:{type}` + PEL + set `tenants` |
| Захват | `SELECT … FOR UPDATE SKIP LOCKED` по `task$` с `lock_expires_on` | `XAUTOCLAIM` по idle-времени (`PendingClaimIdle`) |
| Часы повтора | `lock_expires_on` / `BatchingWindow` | только idle-время в PEL |
| Задержка `Postpone`/`Retry` | учитывается (`lock_expires_on = now + delay`) | **игнорируется**; повтор после `PendingClaimIdle` |
| Лимит попыток провайдера | только consumer-level (`MaxDeliveryAttempts` в courier); задача, застрявшая в `Processing`, может быть арендована повторно | счётчик доставок `XPENDING`, ограничен `RedisOutboxConsumeSettings.MaxDeliveryAttempts` |
| Сохранение статусов | каждый статус пишется в `task$`/`__log$` | только `XACK` / оставить pending / poison |
| Политика «яда» | дедуплицированный `__error$` + опциональный `IPgOutboxPoisonHandler` (`Default` оставляет `__error$`, `Handled` пропускает) | `IRedisOutboxPoisonHandler` (по умолчанию drop + `XACK`); DLQ нет |
| Retention | очистка и миграция партиций (`CleanupSettings`) | встроенный `XTRIM MINID` по безопасной границе, по умолчанию выключен |
| Транзакция публикации | может входить в `PgTransaction` вызывающего | контекста публикации нет (`NotSupportedException`) |
| Схема/миграции | миграции фоновыми job-ами | схемы нет; ключи появляются при первой публикации |
| Журнал доставок | `__log$` | нет; запись stream живёт до retention |
| Старт группы | `__offset$` на `(group, tenant)` | курсор группы (`RedisOutboxConsumeSettings.StartFrom` только при создании) |
| Кластер / HA | репликация/HA PostgreSQL | команды в один ключ, безопасно для Redis Cluster |
| Когда выбирать | транзакционная согласованность и аудит | высокий throughput, append-only лог, простая эксплуатация |

> Провайдеры намеренно **не** взаимозаменяемы на уровне хранилища: смена пакета не переносит
> pending-сообщения. Перед переключением старый провайдер нужно опустошить (drain).

---

## Требования к провайдерам

Провайдер должен реализовать три ключевых интерфейса:

| Интерфейс | Назначение |
|-----------|-----------|
| `IOutboxBulkWriter` | Массовая вставка сообщений в БД |
| `IOutboxDeliveryManager` | Блокировки и диспетчеризация сообщений |
| `IOutboxTenantDetector` | Источник ID арендатора (опционален; базовый контракт — `ITenantSource`) |

### Регистрация и валидация на старте

Пакет-провайдер регистрирует три контракта одним вызовом, который заодно объявляет идентичность,
возможности и проверяет установку на старте:

```csharp
services.AddOutboxProvider(
    new OutboxProviderDescriptor(
        "PostgreSql",
        OutboxProviderCapabilities.PublishContext
        | OutboxProviderCapabilities.TenantDetection
        | OutboxProviderCapabilities.LockExtension
        | OutboxProviderCapabilities.Postponement),
    provider => provider
        .WithBulkWriter<OutboxBulkWriter>()
        .WithDeliveryManager<OutboxDeliveryManager>()
        .WithTenantDetector<OutboxTenantDetector>());
```

`AddOutboxProvider` отвергает второго провайдера с другим именем и провайдера без bulk-writer'а;
построение `IOutboxProviderRegistry` отвергает отсутствие delivery-manager'а при наличии групп и
отсутствие детектора при заявленной capability `TenantDetection`. Провайдер пишется в лог один раз
на старте. Полный контракт — в [`PlugServices/Readme-ru.md`](PlugServices/Readme-ru.md).

> **Владение контекстами.** Память `messages`, переданная в `IConsumer.Consume`, и все контексты
> в ней принадлежат пайплайну на время вызова. После сохранения статусов контексты
> переиспользуются, поэтому потребитель не должен хранить их (или payload, полученный через них)
> после завершения возвращённой задачи. `OutboxPartInfo`, `OutboxTaskDeliveryInfo`,
> `OutboxRentToken` и `OutboxDeliveryMessage<T>` — это `readonly record struct`.
>
> **Форма для провайдеров (0.18).** У `IOutboxBulkWriter` ровно один абстрактный метод — batch с
> общей part-информацией и без обёрток на сообщение; прежние per-message перегрузки удалены.
> `OutboxDeliveryMessage<T>` — плоская запись (`PayloadId`, `Payload`, `PartInfo`, `DeliveryInfo`) с
> явным `RentToken`, который провайдер получает обратно в `IOutboxContext.RentToken`. Подробности и
> миграция — в [`PlugServices/Readme-ru.md`](PlugServices/Readme-ru.md).

### Как сохранить payload после вызова

Забирайте сам объект payload, а не контекст:

```csharp
public async ValueTask Consume(
    OutboxConsumerSettings settings,
    OutboxMessageFilter filter,
    ReadOnlyMemory<IOutboxContextOperations<OrderCreated>> messages,
    CancellationToken ct)
{
    var owned = new List<OrderCreated>(messages.Length);

    foreach (IOutboxContextOperations<OrderCreated> message in messages.Span)
    {
        // Ссылка (или значение, если TMessage — struct) с этого момента принадлежит вам.
        owned.Add(message.Payload);
        message.Ok();
    }

    // После возврата из Consume контексты переиспользуются; `owned` — нет.
    await _inbox.SaveAsync(owned, ct);
}
```

Если `TMessage` mutable или кастомный `IOutboxMessageSerializer` может вернуть pooled/reused
буфер, клонируйте данные прямо внутри `Consume`, а не храните ссылку:

```csharp
byte[] owned = JsonSerializer.SerializeToUtf8Bytes(message.Payload);
```

Не храните сам `IOutboxContextOperations<T>` (или срез `messages`) в поле, статической
коллекции, фоновой задаче или logging-scope.

---

## Зависимости

- **Sa.Schedule** — планировщик фоновых заданий
- Базовые классы из **Sa** (LockRenewer, MurmurHash3, Retry, расширения)

---

## Лицензия

MIT
