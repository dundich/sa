# Sa.Outbox.PostgreSql

AOT-библиотека для реализации паттерна **Transactional Outbox** с использованием PostgreSQL в .NET приложениях.
Обеспечивает гарантированную доставку сообщений с поддержкой мультитенантности, конкурентной обработки и расширенным управлением консьюмерами.

## Содержание

- [На что это похоже](#на-что-это-похоже) — сравнение модели с Redis Stream и Kafka
- [Быстрый старт](#быстрый-старт-5-минут)
- [Публикация внутри транзакции вызывающего кода](#публикация-внутри-транзакции-вызывающего-кода)
- [Руководство по настройке](#руководство-по-настройке)
  - [Настройки группы из конфигурации](#настройки-группы-из-конфигурации-configurationkey)
  - [Справочник `OutboxConsumerSettingsBuilder`](#справочник-настроек-outboxconsumersettingsbuilder)
  - [Бюджет латентности сразу для всех групп](#бюджет-латентности-сразу-для-всех-групп) — `WithPollingBudget` / `WithPollingProfile`
  - [Runtime-управление настройками](#runtime-управление-настройками)
- [Жизненный цикл сообщения](#жизненный-цикл-сообщения)
- [Статусы результата](#статусы-результата)
- [Архитектура БД](#архитектура-бд)
- [Массовая вставка](#массовая-вставка)
- [Требования](#требования)
- [Лицензия](#лицензия)

Подробная конфигурация (таблицы, миграции, очистка, `PgPollingSettings`) — в [`Configuration/Readme-ru.md`](Configuration/Readme-ru.md).

### На что это похоже

По модели работы это ближе к **Redis Stream** и **Kafka**, чем к классическому «outbox → брокер»:

| | Redis Stream | Kafka | здесь |
|---|---|---|---|
| Лог | `XADD` в stream | append-only партиция | `__msg$` + `msg_seq` (BIGSERIAL) |
| Группы | `XGROUP` / `XREADGROUP` | consumer group | `consumer_group` в настройках группы |
| Курсор | last-delivered-id на группу | `__consumer_offsets` | `__offset$.group_offset_seq` на `(group, tenant)` |
| Партиции | — | partition | `(tenant, part)` → дневные листья |
| Доставка | PEL + `XACK`, claim по таймауту | poll + commit offset | `task_lock_expires_on` + `msg.Ok()/Warn()/…` |
| Порядок | в пределах stream | в пределах партиции | в пределах `(tenant, part)` |
| Retention | `XTRIM` / `MAXLEN` | по времени или размеру | `DROP PARTITION` по дням |

Чем это **не** является: классическим «положили в outbox, отдельный релей отправил в Kafka/RabbitMQ».
Здесь нет брокера, нет CDC/Debezium и нет процесса-релея — сами таблицы PostgreSQL и есть и лог, и
очередь, а консьюмер читает прямо из базы. Если нужна шина, эту роль может играть эта же схема:
группы потребителей дают тот же fan-out.

Чего нет по сравнению с настоящим брокером:

- сетевого протокола и клиентских библиотек — «брокер» это схема PostgreSQL;
- динамического ребаланса: группы статические (регистрируются в DI), а параллелизм внутри группы —
  это `SKIP LOCKED`, а не раздача партиций;
- глобального порядка: он есть только внутри `(tenant, part)`;
- retention по offset: удаляются целые дневные партиции;
- как шина работает только в пределах одной базы — это общая схема, а не сетевой транспорт.

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
                .Options(ob => ob.Configure(o => o.ConnectionString =
                    "Host=localhost;Database=outbox_db;Username=postgres;Password=postgres")))
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

## Публикация внутри транзакции вызывающего кода

Обычно каждый `Publish(...)` пишет на собственном подключении и сразу коммитится (auto-commit).
Когда запись в outbox должна закоммититься **атомарно** с вашими собственными изменениями в БД
(классическая транзакционность outbox), публикуйте внутри `NpgsqlTransaction`, которым управляете вы:

```csharp
using Sa.Outbox.PostgreSql.Publication;   // PgOutboxPublisherExtensions
using Sa.Outbox.Publication;
using Sa.Data.PostgreSql;                 // IPgDataSource / PgTransaction

// Транзакцию открывает базовый data source PostgreSQL:
IPgDataSource dataSource = services.GetRequiredService<IPgDataSource>();
await using PgTransaction sqlTx = await dataSource.BeginTransaction(ct);

// Строки outbox уходят в транзакцию вызывающего кода:
await publisher.PublishInTransaction(events, tenantId: 1, sqlTx.Transaction, ct);

// ... ваши собственные INSERT на sqlTx.Connection / sqlTx.Transaction ...

await sqlTx.CommitAsync(ct);     // строки outbox становятся видимыми атомарно с вашими изменениями
// -- dispose без commit (или RollbackAsync) откатывает всё, включая строки outbox
```

`PgOutboxPublisherExtensions` добавляет типизированные `PublishInTransaction(...)` /
`PublishSingleInTransaction(...)`, принимающие `NpgsqlTransaction` (у `PgTransaction` это
`sqlTx.Transaction`; отдельной перегрузки для обёртки нет — она дублировала бы весь набор методов).
Имя отличается от `Publish`, потому что одноимённый метод экземпляра всегда перекрывает extension-метод —
типизированный вызов должен быть явно виден в коде. Тогда binary COPY выполняется на подключении этой транзакции, внутри неё,
и **обходит слой автоматических повторов** — повторять поверх чужой или прерванной транзакции
недопустимо, поэтому ошибки пробрасываются вызывающему коду.

Если контекст не передан — работает обычный auto-commit. Специально **нет никакого ambient/implicit
scope** — транзакция всегда передаётся явным аргументом.

Проверки:

- Транзакция обязана принадлежать **той же базе данных** (host / port / database), что и data source
  outbox — чужая транзакция выбрасывает `InvalidOperationException` до записи чего-либо.
- Подключение должно быть открытым, а транзакция — его собственной активной транзакцией; иначе
  `InvalidOperationException`.

> **Служебные записи** (`EnsureMsgParts`) всегда выполняются на собственном подключении outbox,
> вне вашей транзакции, поэтому после отката публикации могут остаться пустая партиция (или строка
> в реестре типов) — безвредный мусор, который убирают штатные задачи очистки.
>
> **Про блокировки:** пока ваша транзакция открыта и уже писала в партицию, DDL-партиционирование из
> другой публикации на том же data source (например, первое сообщение нового тенанта) может ждать
> завершения вашей транзакции. Коммитьте (или откатывайте) до того, как продолжите публиковать вне
> транзакции.

---

## Руководство по настройке

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

### Выключение провайдера

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

### Повторная регистрация того же ключа

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

#### Настройки группы из конфигурации (`configurationKey`)

Явные перегрузки `AddDelivery` / `AddDeliveryScoped` с `consumerGroupId` принимают опциональный `configurationKey` — путь к секции `IConfiguration` с настройками этой группы (биндится в `PgOutboxDeliveryOptions`; см. [Configuration/Readme-ru.md](Configuration/Readme-ru.md#pgoutboxdeliveryoptions)):

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

#### Справочник настроек `OutboxConsumerSettingsBuilder`

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
| `WithRetryCountOnError(n)` | 0 | Повторы при ошибке (-1 = бесконечно) |
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

#### Бюджет латентности сразу для всех групп

Вместо повторения `Interval` / `IdleMaxInterval` / `InitialDelay` в каждом `AddDelivery` их можно задать один раз на провайдере. Это дефолты — секция группы и её callback по-прежнему побеждают:

```csharp
services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(...)
    .WithPollingBudget(TimeSpan.FromMinutes(1)));   // простаивающая группа: ~48 с худший случай
// или: .WithPollingProfile(PgPollingProfile.Quiet | Balanced | Responsive)
```

`WithPollingBudget` делит бюджет как `BatchingWindow (3 с) + reserve (max(5 с, 20%)) + IdleMaxInterval` и ставит `MaxProcessingIterations = -1`, чтобы всплеск дренировался в том же цикле. Таблицы значений — в `Configuration/Readme-ru.md` → `PgPollingSettings`. Без вызова этих методов не регистрируется ничего.

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
        .Options(ob => ob.Configure(o => o.ConnectionString =
            "Host=...;Database=...;Username=...;Password=...")
        // Пулинг, таймауты и search path задаются внутри самой строки подключения, например:
        // "Host=...;Minimum Pool Size=5;Maximum Pool Size=100;Search Path=outbox"
    ))
    // ...
)
```

### 4. Сериализация сообщений

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

### 5. Обработчик «ядовитых» сообщений (permanent failures)

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

#### `Handled` с пустым обработчиком

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

### 6. Настройки таблиц

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

### 7. Управление задачами вручную

По умолчанию миграция и очистка работают как фоновые задания через `Sa.Schedule`. Можно управлять вручную:

```csharp
// Сервис миграции outbox живёт в собственном DI-скопе outbox ("outbox").
var migrationService = host.Services.GetRequiredKeyedService<IMigrationService>(OutboxScope.DefaultKey);
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
Publish(messages)
      │  COPY (BINARY) — одна транзакция + advisory-lock (tenant, part)
      ▼
  outbox__msg$          ← сообщение лежит здесь, msg_seq = nextval()
      │
      │ ① materialize — один раз на каждую группу-потребителя
      │    INSERT INTO outbox … SELECT … WHERE msg_seq > @offset
      │    под advisory-lock (consumer_group, tenant);
      │    __offset$ сдвигается в этой же транзакции
      ▼
    outbox  (task queue)   ← строка на каждую пару «сообщение × группа»
      │
      │ ② rent — SELECT … FOR UPDATE SKIP LOCKED
      │    → delivery_status_code = Processing, task_lock_expires_on
      ▼
  IConsumer<T>.Consume
      │
      │ ③ finish — ReturnDelivery
      │    __log$: запись на каждую доставку
      │    __error$: только терминальные ошибки
      │    outbox: статус результата + счётчик попыток
      ▼
  строка остаётся в outbox со статусом и больше не арендуется
  (статусы Pending/Processing/Postpone/Retry/Warn — арендуются снова);
  физически удаляется только вместе с дневной партицией при retention
```

Важно: `__offset$` — это «водяной знак материализации», а не «доставки». Он сдвигается
в момент создания задач, до того как потребитель их увидел. Это безопасно: после сдвига задачи
уже лежат в `outbox`, поэтому падение процесса между материализацией и доставкой ничего не теряет —
задачи просто будут арендованы позже.

### Группы потребителей

Каждая группа — это независимая копия потока. Сообщение из `__msg$` материализуется **по одной
строке на каждую группу**, которая подписана на его тип (part + payload type), поэтому группы не
мешают друг другу и не «съедают» сообщения одна у другой:

```
                         outbox__msg$   (общая: LIST(tenant) → LIST(part) → RANGE(день))
                               │
             ┌─────────────────┼─────────────────┐   по копии на группу
             ▼                 ▼                 ▼
      ┌─────────────┐   ┌─────────────┐   ┌─────────────┐
      │  "orders"   │   │   "audit"   │   │  "billing"  │
      │ курсор      │   │ курсор      │   │ курсор      │
      │ (grp,tenant)│   │ (grp,tenant)│   │ (grp,tenant)│
      └──────┬──────┘   └──────┬──────┘   └──────┬──────┘
             ▼                 ▼                 ▼
      очередь задач     очередь задач     очередь задач      ← своя партиция на группу
             │                 │                 │
      IConsumer<Order>  IConsumer<Order>  IConsumer<Invoice>  ← свой обработчик
             │                 │                 │
             ▼                 ▼                 ▼
      __log$ (grp)      __log$ (grp)      __log$ (grp)        ← свой журнал доставок
```

`"orders"` и `"audit"` на рисунке — две группы на **один и тот же** тип сообщения: обе получат все
сообщения, каждая в свою очередь. `"billing"` подписана на другой тип и получает только свои.

Что у группы своё и что общее:

| Ресурс | Где | Гранулярность |
|---|---|---|
| Курсор | `__offset$` | `(consumer_group, tenant)` |
| Очередь задач | `outbox` | `LIST(tenant) → LIST(consumer_group) → RANGE(день)` |
| Журнал доставок | `__log$` | `LIST(tenant) → LIST(consumer_group) → RANGE(день)` |
| Расписание и настройки (`Interval`, `MaxBatchSize`, `MaxProcessingIterations`, `LockDuration`, …) | `DeliveryJob` в планировщике | на группу |
| Лок материализации | advisory-lock | `(consumer_group, tenant)` — группы не блокируют друг друга |
| Сообщения | `__msg$` | общая |
| Реестр типов | `__type$` | общая |
| Терминальные ошибки | `__error$` | общая, без разреза по группам (партиционирована по дню) |

Отсюда практические следствия:

- **потолок потребления считается на группу**, а не на базу: `MaxBatchSize × MaxProcessingIterations`
  за `Interval` (при дефолтах 128 × 10 за минуту ≈ 1 280 сообщений/мин на группу);
- чтобы ускорить доставку, добавляйте группы (разные типы/парты) или поднимайте батч и частоту
  опроса — но не число воркеров на одну группу, оно упирается в её же лок;
- курсор у каждой группы свой, поэтому «отстающая» группа не тормозит остальные;
- сообщение, на тип которого не подписана ни одна группа, остаётся в `__msg$` и уходит только с
  партицией при retention.

---

## Статусы результата

После обработки каждого сообщения вызовите ровно один метод:

| Метод | Когда использовать | Что дальше |
|---|---|---|
| `msg.Ok()` | Всё прошло успешно | Статус записан, строка больше не арендуется (удаляется с партицией) |
| `msg.Created()` | Создан побочный ресурс | Статус записан, строка больше не арендуется |
| `msg.Accepted()` | Принято в асинхронную обработку | Статус записан, строка больше не арендуется |
| `msg.NoContent()` | Обработано, данных нет | Статус записан, строка больше не арендуется |
| `msg.Error(ex)` | Неустранимая ошибка | Запись в `__error$`, без повтора |
| `msg.Warn(ex)` | Временная проблема (сеть, таймаут) | Снова арендуется после окна батчинга |
| `msg.Postpone(ts)` | Нужно подождать перед повтором | Повтор после `ts` |
| `msg.Retry(ts, reason)` | Повтор с метаданными | Повтор с информацией о попытке |
| `msg.Aborted(reason)` | Намеренно пропустить | Отмечено как пропущенное, без повтора |
| `msg.ErrorMaxAttempts()` | Исчерпан максимум попыток | Запись в `__error$`, без повтора |

`Error`/`Error5xx` и `ErrorMaxAttempts` дополнительно передаются в `IPgOutboxPoisonHandler` до
записи статуса; см. [Обработчик «ядовитых» сообщений](#5-обработчик-ядовитых-сообщений-permanent-failures).

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
| **`msg_seq` (BIGSERIAL)** | Порядок вставки, назначаемый базой, и **сам** курсор потребления (`__offset$.group_offset_seq`). Денормализован в таблицу задач, чтобы `RETURNING msg_seq` двигал курсор тем же запросом |
| **UUID v7 message id** | Идентификатор, который генерирует приложение; ключ соединения `__msg$` с очередью задач. Для порядка **не** используется: он приходит из часов приложения, а они убегают и откатываются назад |
| **BINARY COPY** | Максимальная скорость массовой вставки |
| **SKIP LOCKED** | Безопасная конкуренция воркеров за задачи — и только за задачи. На единственной строке курсора «пропустить» означало бы «очередь пуста» при непустом бэклоге |
| **Advisory Locks** | Координация смещений на `(consumer group, tenant)` |
| **Лок публикации** | `pg_advisory_xact_lock` на `(tenant, part)` вокруг COPY — делает порядок `msg_seq` равным порядку коммитов, чтобы курсор `max(msg_seq)` не перескочил ещё коммитящуюся строку |
| **Индекс курсора** | `(msg_seq)` на `__msg$` — превращает чтение курсора в index range scan вместо Seq Scan всего окна lookback (замерено 56.8 ms → 0.166 ms на 7 × 40k строк) |
| **Дневные партиции** | Pruning оставляет опрос на нескольких листьях, а retention — это `DROP PARTITION` (индексы уходят вместе с листом), а не `DELETE` |
| **murmurHash3** | Компактная идентификация типов, кэшированная в `__type$` |
| **SqlCacheSplitter** | Дробит крупные многострочные INSERT/UPDATE на чанки до общей `DefaultMaxLen` (1024) |

---

## Массовая вставка

### Правила

1. **Батч 256–1024, не меньше.** Пропускная способность растёт с батчем (4.7k → 58k rows/s без лока),
   потому что каждая публикация платит фиксированную цену: BEGIN, лок, COPY, fsync на COMMIT.
   Одна публикация = один `COPY` = одна транзакция.
2. **Один продьюсер на парт.** Лок сериализует не только COPY, но и коммиты парта — group commit
   перестаёт амортизировать fsync: 8 продьюсеров в один парт дают 14.8k rows/s против 55.3k без
   лока (−67 %). При одном продьюсере на парт цена нулевая; 8 партов дают 43.7k rows/s и p50 32 ms
   против 23.5k/77 ms на трёх.
3. **Не смешивайте парты/тенанты в одном вызове `Publish`** — иначе один `COPY` берёт N локов и
   держит их все до коммита.
4. **Короткие транзакции.** Публикация внутри бизнес-транзакции держит лок парта всё её время.
   Атомарность требует того же транзакционного контекста — значит бизнес-транзакция должна быть
   короткой, а не «на 5 секунд с внешним HTTP внутри».
5. **Не добавляйте индексы в `__msg$`.** Каждый стоит ~140–200 B WAL/row и 30–50 % скорости. Нужен
   ровно один — курсорный `(msg_seq)`. PK — самый дорогой; убирать его можно только осознанно
   (потеря защиты от дублей `msg_id`).
6. **Не пишите `COPY` напрямую в лист** — замерено: не быстрее (72–78k против 87k), routing не
   узкое место.
7. **`msg_seq` не трогать.** Sequence даёт ~4 B WAL/row и нулевой вклад в потолок. `CACHE` ломает
   курсор (потеря сообщений).
8. **Сервер:** `wal_compression=lz4` (или `zstd`) — WAL это самая большая статья, `UNLOGGED`-контроль
   показал запас до 2.9×; `max_wal_size`/`checkpoint_timeout` чтобы не ловить частые checkpoint'ы;
   `full_page_writes` оставить `on`; `synchronous_commit` оставить `on`.
9. **Пул соединений:** `pool_mode=transaction` (не `statement` — библиотека работает в транзакциях).
   Локи транзакционные, PgBouncer их не ломает.
10. **Партиции:** много дневных партиций вставке не мешают, а retention через `DROP PARTITION`
    бесплатен (индексы уходят вместе с листом).

### Замеры

Стенд: PostgreSQL 15 в Docker, `__msg$` = `LIST(tenant) → LIST(part) → RANGE(day)`, 256 листьев,
PK `(msg_id, tenant_id, msg_part, msg_created_at)` + курсорный индекс `(msg_seq)`, payload 200 B,
`synchronous_commit=on` (реальный режим), fsync ≈ 11 ms. Клиент — Npgsql 10,
`NpgsqlBinaryImporter`, ровно как в `BulkInsertMsgCommand`. 8 продьюсеров, 8-секундные прогоны,
медиана из 3.

Абсолютные числа — свойство стенда. Переносите их через формулу в конце раздела, а не напрямую;
разброс между прогонами на этом стенде ±30 % (латентности стабильнее, чем throughput).

**Батч и цена лока** — 8 продьюсеров, один парт:

| batch | без лока, rows/s | с локом, rows/s | цена лока |
|---|---|---|---|
| 16 | 4 685 | 1 198 | −74 % |
| 64 | 17 578 | 3 524 | −80 % |
| 256 | 44 946 | 14 808 | −67 % |
| 1024 | 58 450 | 26 222 | −55 % |

**Сценарии** — batch 256, лок включён (конфигурация «1 парт» — строка 256 из таблицы выше,
измерена в другом прогоне):

| партов | rows/s | tx/s | p50, ms | p99, ms |
|---|---|---|---|---|
| 1 | 17 183 | 67 | 99.1 | 413.4 |
| 3 | 23 498 | 92 | 77.5 | 156.3 |
| 8 | 43 683 | 171 | 32.2 | 176.0 |

**WAL на строку** — batch 1024, 8 продьюсеров:

| таблица | WAL/row |
|---|---|
| PK + cursor index | 526 B |
| только cursor index | 386 B |
| без индексов | 319 B |
| `UNLOGGED` (контроль) | 3 B |

**Расшифровка колонок:**

- `batch` — строк в одном `COPY`, т.е. в одной публикации и одной транзакции
  (`OutboxPublishSettings.MaxBatchSize`).
- `rows/s` — строк в секунду суммарно по всем продьюсерам.
- `tx/s` — транзакций (публикаций) в секунду; при известном `batch` это `rows/s / batch`.
- `без лока` / `с локом` — до и после введения advisory-lock на `(tenant, part)`; `цена лока` —
  потеря пропускной способности.
- `партов` — сколько разных `(tenant, part)` задействовано; 8 продьюсеров на 3 парта = по ~2.7
  продьюсера на ключ.
- `p50` / `p99` — перцентили латентности одной публикации на клиенте, включая ожидание лока.
- `WAL/row` — байт WAL на одну вставленную строку.

**Формула для переноса на своё железо:**

```
t_txn ≈ F + batch × t_row
rows/s на парт ≈ batch / t_txn
rows/s всего   ≈ min(N_партов × rows/s на парт, потолок сервера)
```

`F` — эффективная цена одного коммита в вашей инсталляции (с учётом group commit), `t_row` ≈ 0.02 ms
на строку. Обе величины снимаются двумя точками по таблице «батч»: `1/R(B) = F/B + t_row`, поэтому
по `B` = 256 и `B` = 1024 (`R` = 44 946 и 58 450) получаем `F ≈ 1.8 ms` и `t_row ≈ 15 µs` — это
эффективные значения для 8 продьюсеров, а не «сырой» fsync диска.

### Что мерить в проде

- `xact_commit/s` и `rows/s` по `__msg$` — фактическая стоимость публикации;
- WAL bytes/row (`pg_current_wal_lsn` до/после окна) — ожидаемо ~350–550 B; рост означает лишние
  индексы или bloat;
- `pg_stat_activity.wait_event` — `Lock:advisory` дольше нескольких ms означает перегруз ключа
  (слишком много продьюсеров на парт);
- лаг доставки: `max(msg_seq) − group_offset_seq` по каждой группе;
- p99 публикации на клиенте, а не только серверное время.

---
## Миграция 0.17 → 0.18 (существующие развёртывания)

**Схема БД не меняется.** Таблицы, колонки, партиции, `__offset$` и курсор `msg_seq` те же;
`OutboxRentToken` живёт только в памяти. Миграция данных не требуется.

### Что поменялось в коде

| Было | Стало |
|---|---|
| `IOutboxBulkWriter`: 4 перегрузки, абстрактна legacy | один абстрактный `InsertBulk(batch, context, ct)`; legacy-перегрузки и `OutboxMessage<T>` удалены |
| `OutboxDeliveryMessage<T>(OutboxId, OutboxMessage<T>, DeliveryInfo)` | плоская запись `(OutboxId, PayloadId, Payload, PartInfo, DeliveryInfo, RentToken)` |
| `IOutboxPlugin` (закомментирован) | `AddOutboxProvider(descriptor, builder)` |
| — | `IOutboxProviderRegistry`, `IOutboxPublisherResolver`, `IOutboxHealth` |

- **Свой writer** (если был): реализуйте `InsertBulk(batch, context, ct)` и зарегистрируйте через
  `AddOutboxProvider(...).WithBulkWriter(sp => new MyWriter(...))`.
- **Свой консьюмер** продолжает работать: строковые ключи и строковые методы
  `IOutboxConsumerManager` сохранены.
- Штатные пакеты PG/Redis уже переведены на новые контракты — приложение, которое пользуется
  только `AddSaOutbox`/`AddSaOutboxUsingPostgreSql` и `IOutboxMessagePublisher`, правок не требует.

### Регистрация

- Default provider key остаётся `"outbox"`; для него сохранены unkeyed-алиасы:
  `IOutboxMessagePublisher`, `IOutboxProviderRegistry`, `PgOutboxSettings` (+ компоненты),
  `PgPollingSettings`, `IDatabase` (Redis), три storage-контракта. Старый код, резолвящий их без
  ключа, продолжает работать.
- Новый односрочный overload: `AddSaOutboxUsingPostgreSql(cfg, outbox)`, `scopeKey` пишется один раз.
- `WithOutboxSettings`/`WithPolling` — теперь действия **своего** провайдера; одна и та же
  регистрация не влияет на второй провайдер.

### Если добавляете второй PG-провайдер

1. Дайте ему **свою схему** (или свои имена таблиц): `WithOutboxSettings((_, s) => s.TableSettings.WithSchema("billing"))`.
2. Стартовая проверка `PgMultiProviderGuard` падает, если два провайдера указывают на одни и те же
   таблицы (иначе каждое сообщение доставлялось бы дважды), и на одном `(host, port, database)` с
   одинаковой схемой/таблицами.
3. Миграция партиций второго провайдера — его собственный job (создаст листья в своей схеме).
4. Consumer-группы с одинаковыми id у разных провайдеров теперь допустимы: менеджер и консьюмеры
   адресуются `(providerKey, groupId)`; строковые методы бросают ошибку неоднозначности.

### Если переводите существующий outbox на не-дефолтный key

- PG хранилище не меняется: те же схема/таблицы/курсоры, поэтому накопленные сообщения и offset'ы
  читаются как раньше.
- Data source становится keyed (`AddSaPostgreSqlDataSource(key, ...)`); заимствованный unkeyed
  `NpgsqlDataSource` по-прежнему находится через `PgDataSourceResolver`.
- Расписание и partitional-модуль переезжают под новый scope key.

### Откат

0.18 не пишет новую схему, поэтому откат на 0.17 безопасен. Единственный след — таблицы второго
провайдера, если он успел создать партиции; они просто останутся неиспользуемыми.

> Redis: перевод существующего провайдера на не-дефолтный ключ меняет префикс стримов
> (`sa:outbox` → `sa:outbox:{key}`). Задайте префикс явно через `WithOutboxSettings`, чтобы сохранить
> старые ключи.


## Требования

- **.NET 10.0** или выше
- **PostgreSQL 15+**
- Совместимо с **Native AOT**

## Лицензия

MIT
