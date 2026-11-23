# Пошаговая настройка

**Русский** | [English](Readme.md)

Пошаговая настройка PostgreSQL-провайдера: тенанты, регистрация консьюмеров, подключение, сериализация, обработка «ядовитых» сообщений, настройки таблиц и ручной запуск задач. Часть документации [`Sa.Outbox.PostgreSql`](../Readme-ru.md); справочник настроек — в [`Configuration/Readme-ru.md`](../Configuration/Readme-ru.md).

## Содержание

- [Выключение провайдера](#выключение-провайдера)
- [Повторная регистрация того же ключа](#повторная-регистрация-того-же-ключа)
- [1. Настройка тенантов](#1-настройка-тенантов)
- [2. Регистрация консьюмеров](#2-регистрация-консьюмеров)
- [Настройки группы из конфигурации (`configurationKey`)](#настройки-группы-из-конфигурации-configurationkey)
- [Справочник настроек `OutboxConsumerSettingsBuilder`](#справочник-настроек-outboxconsumersettingsbuilder)
- [Бюджет латентности сразу для всех групп](#бюджет-латентности-сразу-для-всех-групп)
- [Runtime-управление настройками](#runtime-управление-настройками)
- [3. Подключение к PostgreSQL](#3-подключение-к-postgresql)
- [4. Сериализация сообщений](#4-сериализация-сообщений)
- [5. Обработчик «ядовитых» сообщений (permanent failures)](#5-обработчик-ядовитых-сообщений-permanent-failures)
- [6. Настройки таблиц](#6-настройки-таблиц)
- [7. Управление задачами вручную](#7-управление-задачами-вручную)

Регистрация — один вызов или два:

| Вызов | Назначение |
|---|---|
| `AddSaOutbox(...)` | Ядро: тенанты, консьюмеры, расписание |
| `AddSaOutboxUsingPostgreSql(cfg)` | PostgreSQL: подключение, таблицы, миграция, очистка |
| `AddSaOutboxUsingPostgreSql(cfg, outbox)` | **Один вызов**: ядро + провайдер, `scopeKey` пишется один раз |

Одна регистрация вместо двух:

```csharp
services.AddSaOutboxUsingPostgreSql(
    pg => pg.WithDataSource(...).WithPollingBudget(TimeSpan.FromMinutes(1)),
    outbox => outbox
        .WithTenants((_, t) => t.WithTenantIds(1, 2, 3))
        .WithDeliveries(d => d.AddDelivery<OrderConsumer, OrderCreated>("orders")));
```

> **Изоляция.** Outbox регистрирует своё внутреннее расписание `Sa.Schedule` и свой модуль
> `Sa.Partitional.PostgreSql` под ключом скопа `"outbox"` (`Sa.Outbox.OutboxScope.DefaultKey`) и
> никогда не трогает расписание/партициальный модуль приложения по умолчанию. Оба входных метода
> принимают необязательный последний параметр `scopeKey`. В форме одного вызова
> `AddSaOutboxUsingPostgreSql(cfg, outbox, scopeKey)` он задаётся один раз; при раздельной
> регистрации передайте одно и то же значение в `AddSaOutbox` и `AddSaOutboxUsingPostgreSql`.
> Зарезервированный ключ `"default"` отклоняется.

## Выключение провайдера

`WithEnabled(false)` — **регистрационный** выключатель, а не runtime-флаг:

```csharp
services.AddSaOutboxUsingPostgreSql(pg => pg
    .WithEnabled(configuration.GetValue("Outbox:PostgreSql:Enabled", true))
    .WithDataSource(...));
```

Колбэк конфигурации выполняется всегда, но в контейнер не попадает ничего: ни настройки, ни
keyed data source (поэтому `ValidateOnStart` не упадёт на отсутствующей строке подключения), ни
переопределения сериализатора/poison, ни consumer-группы, ни задачи `Sa.Schedule`. `Enabled` по
умолчанию `true`; выключенный провайдер не оставляет следа в DI, поэтому регистрацию можно держать в
коде для всех окружений и гасить конфигурацией. Провайдеры под другими ключами не затрагиваются. В
раздельной форме (`AddSaOutbox` + этот вызов) ядро всё равно регистрирует consumer-группы, поэтому
выключенный провайдер уронит стартовую проверку `scopeKey` с их именами в сообщении — не вызывайте
`AddSaOutbox` или используйте форму одного вызова.

## Повторная регистрация того же ключа

Повторный `AddSaOutboxUsingPostgreSql(..., scopeKey)` для одного ключа допустим: контракты
провайдера, стартовый валидатор, миграционный guard, регистрации consumer-групп и расписание
дедуплицируются. Колбэки настроек outbox и polling из последней регистрации заменяют ранее
записанные, поэтому выполняются один раз — а не по разу на регистрацию. Подключение принадлежит
**первой** регистрации data source — callback второго вызова *outbox* игнорируется вместо
исключения, поэтому соединение настраивайте один раз.

Data source, зарегистрированный под тем же ключом другим модулем, переиспользуется, а не
дублируется: «голый» `WithDataSource()` его заимствует. `WithDataSource(configure)` — это явное
намерение задать подключение, и оно применяется к этому ключу; если другой модуль его уже настроил,
`AddSaPostgreSqlDataSource` сообщит о дубликате, вместо того чтобы outbox молча оставил чужое
подключение.

## 1. Настройка тенантов

Внутри `AddSaOutbox`:

```csharp
.AddSaOutbox(builder => builder
    .WithTenants((_, ts) => ts
        .WithTenantIds(1, 2, 3)                         // Фиксированный список
        // .WithAutoDetect()                             // Автоопределение из сообщений
        // .WithTenantSource(myTenantSource)             // Свой ITenantSource
        // .WithTenantProvider(ct => ...)                // Func<CancellationToken, ValueTask<int[]>>
    )
    // ...
)
```

Детектор для `WithAutoDetect()` поставляет пакет-провайдер; чтобы заменить его, зарегистрируйте свой
на уровне провайдера (`AddOutboxProvider(..., p => p.WithTenantDetector<MyDetector>())`). Параллелизм
по тенантам — настройка консьюмер-группы: `WithPerTenantMaxDegreeOfParallelism(4)` у её
`OutboxConsumerSettingsBuilder` (см. ниже).

## 2. Регистрация консьюмеров

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

## Настройки группы из конфигурации (`configurationKey`)

Явные перегрузки `AddDelivery` / `AddDeliveryScoped` с `consumerGroupId` принимают опциональный `configurationKey` — путь к секции `IConfiguration` с настройками этой группы (биндится в `PgOutboxDeliveryOptions`; см. [Configuration/Readme-ru.md](../Configuration/Readme-ru.md#pgoutboxdeliveryoptions)):

```csharp
.AddDeliveryScoped<OrderConsumer, OrderCreated>(
    "cg_order_consumer",
    (_, settings) => settings
        .StartImmediately()
        .WithMaxBatchSize(16),
    configurationKey: "DeliveryGroups:cg_order_consumer")
```

Настройки группы складываются из трёх слоёв, от низшего к высшему:

```
OutboxDefaults  →  секция конфигурации  →  callback configure
```

- **Какая секция** — по умолчанию конвенция `Outbox:DeliveryGroups:{groupId}`; явный `configurationKey` её перекрывает, а `configurationKey: ""` отключает чтение секции для этой группы. Префикс конвенции меняется `IOutboxBuilder.WithDeliveryConfigurationSection(...)`, `null` выключает её.
- **Ключ задан** — non-null значения секции вставляются между дефолтами и callback `configure`; частично заполненная секция переопределяет только то, что в ней названо.
- **Группа без ключа** читает конвенционную секцию, если она существует; секции под другими префиксами не читаются.
- Отсутствующая или пустая секция — no-op.
- `Paused` — runtime-состояние (`IOutboxConsumerManager`), `AsSingleton` фиксируется тем, какой метод вы вызываете, а identность группы привязана к явному аргументу `consumerGroupId` — ни одно из них не берётся из секции.

Достаточно, чтобы секция входила в `IConfiguration`, зарегистрированный в DI (например, appsettings.json при `Host.CreateDefaultBuilder`); провайдер читает её лениво при старте — изменение секции и рестарт и есть workflow конфигурации.

## Справочник настроек `OutboxConsumerSettingsBuilder`

Единый fluent-билдер для всех параметров consumer group:

| Метод | По умолчанию | Описание |
|---|---|---|
| `WithInterval(span)` | 1 мин | Период опроса |
| `StartImmediately()` | — | Старт без ожидания первого интервала |
| `WithMaxBatchSize(n)` | 128 | Макс. сообщений за батч |
| `WithMaxDeliveryAttempts(n)` | 3 | Стоп-повторы после N попыток |
| `WithLockDuration(span)` | 10 с | TTL блокировки сообщения |
| `WithLockRenewal(span)` | 3 с | Период продления блокировки |
| `WithLookbackInterval(span)` | 7 дн | История поиска необработанных |
| `WithBatchingWindow(span)` | 3 с | Окно агрегации сообщений |
| `WithNoBatchingWindow()` | — | Взять всё доступное сейчас |
| `WithConcurrencyLimit(n)` | 1 | Одновременных задач |
| `WithMaxConcurrency(n)` | 48 | Макс. параллельных процессоров |
| `WithRetryCountOnError(n)` | 0 | Повторы job'а при ошибке; `WithInfiniteRetries()` ставит `int.MaxValue`, отрицательные значения отклоняются |
| `WithNoRetries()` | — | Отключить повторы |
| `WithInfiniteRetries()` | — | Бесконечные повторы |
| `WithMaxProcessingIterations(n)` | 10 | Итераций за цикл (-1 = безлимитно) |
| `WithSingleIteration()` | — | Одна итерация (тестирование) |
| `WithUnlimitedIterations()` | — | Безлимитные итерации |
| `WithIterationDelay(span)` | 0 с | Задержка между итерациями |
| `WithPerTenantTimeout(span)` | 0 | Таймаут обработки одного тенанта |
| `WithPerTenantMaxDegreeOfParallelism(n)` | 1 | Параллельность по тенантам (1 = последовательно, -1 = все ядра) |
| `WithTenantSequentialProcessing()` | 1 | Последовательная обработка по тенантам |
| `WithTenantMaxParallelism()` | -1 | Максимальная параллельность по тенантам |
| `Paused(bool)` | false | Пауза consumer group |
| `Resumed()` | — | Снять с паузы |
| `AsSingleton(bool)` | true | Singleton (один на кластер) vs Scoped |

## Бюджет латентности сразу для всех групп

Вместо повторения `Interval` / `IdleMaxInterval` / `InitialDelay` в каждом `AddDelivery` их можно задать один раз на провайдере. Это дефолты — секция группы и её callback по-прежнему побеждают:

```csharp
services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(...)
    .WithPollingBudget(TimeSpan.FromMinutes(1)));   // простаивающая группа: ~48 с худший случай
// или один из: .WithPollingProfile(PgPollingProfile.Quiet / Balanced / Responsive)
```

`WithPollingBudget` делит бюджет как `BatchingWindow (3 с) + reserve (max(5 с, 20%)) + IdleMaxInterval` и ставит `MaxProcessingIterations = -1`, чтобы всплеск дренировался в том же цикле. Таблицы значений — в [`Configuration/Readme-ru.md` → `PgPollingSettings`](../Configuration/Readme-ru.md#pgpollingsettings). Без вызова этих методов не регистрируется ничего.

## Runtime-управление настройками

Настройки автоматически регистрируются в `IOutboxConsumerManager` при первом запуске job'а. Для runtime-изменений:

```csharp
var manager = host.Services.GetRequiredService<IOutboxConsumerManager>();

// Atomic swap — новый снимок применяется на следующей итерации
manager.Apply("cg_order_consumer", s => s with { MaxBatchSize = 64 });

// Pause / Resume
manager.Pause("cg_order_consumer");
manager.Unpause("cg_order_consumer");

// Подписка на изменения
using var sub = manager.Subscribe("cg_order_consumer", updated =>
{
    // реакция на изменение настроек
});

// Проверка состояния
bool paused = manager.IsPaused("cg_order_consumer");
```

> **Self-bootstrapping:** `DeliveryJob` автоматически регистрирует настройки в `IOutboxConsumerManager` при первом запуске. Используется `TryRegister` для безопасной конкурентной регистрации — если несколько инстансов приложения стартуют одновременно, только один успешно зарегистрирует настройки, остальные прочитают канонический снимок из менеджера.

## 3. Подключение к PostgreSQL

Внутри `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds
        .Options(ob => ob.Configure(o => o.ConnectionString =
            "Host=...;Database=...;Username=...;Password=...")
        // Пулинг, таймауты и search path задаются внутри самой строки подключения, например:
        // "Host=...;Minimum Pool Size=5;Maximum Pool Size=100;Search Path=outbox"
    ))
    // ...
)
```

## 4. Сериализация сообщений

`IOutboxMessageSerializer` живёт в ядре (`Sa.Outbox.Serialization`); ядро регистрирует
`JsonOutboxMessageSerializer` (UTF-8 JSON) как fallback, поэтому этот раздел необязателен.
Сериализатор, заданный здесь, регистрируется **за своим ключом провайдера**: другой провайдер
сохраняет свой (или общий уровня контейнера), поэтому в одном контейнере могут сосуществовать разные
кодеки. Задавайте его на уровне ядра (`AddSaOutbox(b => b.WithMessageSerializer(...))`), если все
провайдеры должны делить один.

Внутри `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithMessageSerializer(OrderSerializer.Instance)   // синглтон-экземпляр
    // .WithMessageSerializer<MySerializer>()                   // DI-resolved синглтон
    // .WithMessageSerializer(sp => sp.GetRequiredService<MySerializer>())  // фабрика
)
```

### AOT-совместимый сериализатор

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

## 5. Обработчик «ядовитых» сообщений (permanent failures)

`__error$` и строка задачи уже сохраняют любую терминальную ошибку, поэтому dead-letter очередь
провайдеру не нужна. Если приложению нужен собственный sink — переслать payload в другую очередь,
object storage или поднять алерт — зарегистрируйте `IPgOutboxPoisonHandler`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithPoisonHandler<MyPoisonHandler>()                              // из DI
    // .WithPoisonHandler(new MyPoisonHandler())                       // экземпляр
    // .WithPoisonHandler(sp => new MyPoisonHandler(sp.GetRequiredService<MyStore>()))
)
```

Обработчик получает `PgOutboxPoisonMessage`: `OutboxId`, `TaskId`, `PayloadId`, `PayloadType`,
`ConsumerGroup`, `TenantId`, `Part`, `Attempt`, `StatusCode`, `StatusMessage`, `Exception` и payload,
сериализованный сериализатором ядра `IOutboxMessageSerializer`.

Он вызывается для каждого `Error`/`Error5xx` и для `MaximumAttemptsError`, **до** финализации задачи,
а возвращаемое значение выбирает стратегию:

| Возврат | Результат |
|---|---|
| `PgOutboxPoisonAction.Default` | встроенная стратегия: строка `__error$` + задача с `error_id` |
| `PgOutboxPoisonAction.Handled` | обработчик берёт ошибку на себя: `__error$` не пишется; задача всё равно финализируется как `Error`/`MaximumAttemptsError`, но с `error_id = NULL` |
| исключение | батч не финализируется, задача остаётся `Processing`, лок истекает → повторная аренда и повторный вызов (at-least-once) |

Стандартный `NoOpPgOutboxPoisonHandler` возвращает `Default` — поведение остаётся прежним.

### `Handled` с пустым обработчиком

Если вернуть `Handled` и ничего не сделать, это **выбрасывание, после которого остаётся только скелет**:

| Что | Результат |
|---|---|
| `__error$` | не пишется |
| `__log$` (журнал доставок) | строка всё равно пишется: статус `Error`/`508`, `status_message`, `payload_id`, группа, tenant, `task_id`, `transact_id`, `error_id = NULL` |
| `task$` | терминальный `Error`/`Error5xx`/`508`, `error_id = NULL`, `delivery_attempt + 1`, `lock_expires_on` обновлён |
| `msg$` | строка с payload остаётся без изменений |
| повторная доставка | нет — `Error` не входит в набор статусов аренды |

То есть payload физически не теряется: его ещё можно найти через `task$`/`__log$` по `msg_payload_id`, а
затем через `msg$` по `msg_id`, пока cleanup не удалит партицию. Теряется именно запись об ошибке: в
`__error$` нет ни типа исключения, ни стека, а `status_message` содержит только текст, переданный
consumer-ом в `Error(...)`.

| Результат handler-а | `__error$` | `task$` | Повтор | Данные |
|---|---|---|---|---|
| `Default` | пишется (дедуп) | `Error`, `error_id` | нет | payload в `msg$`, ошибка в `__error$` |
| `Handled` + своё хранилище | нет | `Error`, `error_id = NULL` | нет | payload у вас |
| `Handled` + ничего | нет | `Error`, `error_id = NULL` | нет | payload только в `msg$`/`__log$` до cleanup |
| исключение | нет | остаётся `Processing` | да, после истечения лока | payload в `msg$` |

Возвращайте `Handled` только после успешной записи в sink. Если запись не удалась — либо `throw`
(задача останется арендуемой и handler вызовется снова), либо `Default` (провайдер запишет `__error$`).

## 6. Настройки таблиц

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
        settings.TableSettings.TaskQueue.Fields.TaskId = "id";
        settings.TableSettings.TaskQueue.Fields.TenantId = "client_id";
        settings.TableSettings.TaskQueue.Fields.ConsumerGroup = "grp";

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
        // settings.ConsumeSettings.WithMinOffset("cg_order_consumer", DateTimeOffset.Now);
    })
)
```

### Обзор таблиц

| Свойство | Имя по умолчанию | Роль | FillFactor |
|---|---|---|---|
| `Message` | `outbox__msg$` | Исходные сообщения (BINARY COPY) | 100 |
| `TaskQueue` | `outbox` | Активная очередь задач (SKIP LOCKED) | 65 |
| `Delivery` | `outbox__log$` | История доставки | 100 |
| `Error` | `outbox__error$` | Перманентные ошибки | 100 |
| `Type` | `outbox__type$` | Кэш тип ↔ хеш | 100 |
| `Offset` | `outbox__offset$` | Смещения консьюмер-групп | 100 |

### Стратегия партиционирования

| Таблица | Ключ партиции | Сортировочный ключ |
|---|---|---|
| Message | `tenant_id` + `msg_part` | `msg_created_at` |
| TaskQueue | `tenant_id` + `consumer_group` | `task_created_at` |
| Delivery | `tenant_id` + `consumer_group` | `delivery_created_at` |
| Error | только дата | `error_created_at` |
| Type / Offset | нет | — |

## 7. Управление задачами вручную

По умолчанию миграция и очистка работают как фоновые задания через `Sa.Schedule`. Можно управлять вручную:

```csharp
// Сервис миграции outbox живёт в собственном DI-скопе outbox ("outbox").
var migrationService = host.Services.GetRequiredKeyedService<IMigrationService>(OutboxScope.DefaultKey);
bool ok = await migrationService.WaitMigrationAsync(TimeSpan.FromSeconds(30), ct);

// Или проверить состояние
if (!migrationService.OnMigrated.IsCancellationRequested)
{
    // Ни одна миграция ещё не завершилась: задания доставки придержаны до первой из них
}
```
