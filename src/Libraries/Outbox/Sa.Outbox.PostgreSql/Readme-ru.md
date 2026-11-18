# Sa.Outbox.PostgreSql

**Русский** | [English](Readme.md)

AOT-библиотека для реализации паттерна **Transactional Outbox** с использованием PostgreSQL в .NET приложениях.
Обеспечивает гарантированную доставку сообщений с поддержкой мультитенантности, конкурентной обработки и расширенным управлением консьюмерами.

## Установка

```bash
dotnet add package Sa.Outbox.PostgreSql
```

## Содержание

- [Установка](#установка)
- [На что это похоже](#на-что-это-похоже) — сравнение модели с Redis Stream и Kafka
- [Быстрый старт](#быстрый-старт-5-минут)
- [Публикация внутри транзакции вызывающего кода](#публикация-внутри-транзакции-вызывающего-кода)
- [Настройка](Setup/Readme-ru.md)
  - [Настройки группы из конфигурации (`configurationKey`)](Setup/Readme-ru.md#настройки-группы-из-конфигурации-configurationkey)
  - [Справочник настроек `OutboxConsumerSettingsBuilder`](Setup/Readme-ru.md#справочник-настроек-outboxconsumersettingsbuilder)
  - [Бюджет латентности сразу для всех групп](Setup/Readme-ru.md#бюджет-латентности-сразу-для-всех-групп)
  - [Runtime-управление настройками](Setup/Readme-ru.md#runtime-управление-настройками)
- [Жизненный цикл сообщения](Lifecycle/Readme-ru.md)
- [Статусы результата](#статусы-результата)
- [Архитектура БД](#архитектура-бд)
- [Массовая вставка](Bulk/Readme-ru.md)
- [Требования](#требования)
- [Лицензия](#лицензия)

Подробная конфигурация (таблицы, миграции, очистка, `PgPollingSettings`) — в [`Configuration/Readme-ru.md`](Configuration/Readme-ru.md).

У внутренностей провайдера свои readme: [`Commands/Readme-ru.md`](Commands/Readme-ru.md) (команды
доступа к данным и батчинг), [`SqlBuilder/Readme-ru.md`](SqlBuilder/Readme-ru.md) (SQL-шаблоны),
[`TypeResolve/Readme-ru.md`](TypeResolve/Readme-ru.md) (кэш тип ↔ хеш).

## На что это похоже

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
using Sa.Outbox.Serialization;

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
                settings.CleanupSettings.WithRetentionPeriod(TimeSpan.FromDays(30));
            })
            .WithMessageSerializer(JsonOutboxMessageSerializer.Instance) // дефолт ядра; замените своим
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

## Настройка

Пошаговая настройка — тенанты, регистрация консьюмеров, подключение, сериализация, обработка
«ядовитых» сообщений, таблицы и ручной запуск задач — в отдельном документе: [Setup/Readme-ru.md](Setup/Readme-ru.md).

## Жизненный цикл сообщения

Путь сообщения от `Publish(...)` до `Ok()` — материализация, аренда, статусы и группы
потребителей — в отдельном документе: [Lifecycle/Readme-ru.md](Lifecycle/Readme-ru.md).

## Статусы результата

После обработки каждого сообщения вызовите ровно один метод:

| Метод | Когда использовать | Что дальше |
|---|---|---|
| `msg.Ok()` | Всё прошло успешно | Статус записан, строка больше не арендуется (удаляется с партицией) |
| `msg.Created()` | Создан побочный ресурс | Статус записан, строка больше не арендуется |
| `msg.Accepted()` | Принято в асинхронную обработку | Статус записан, строка больше не арендуется |
| `msg.NoContent()` | Обработано, данных нет | Статус записан, строка больше не арендуется |
| `msg.Fail(ex)` | Неустранимая ошибка | Запись в `__error$`, без повтора |
| `msg.Warn(ex)` | Временная проблема (сеть, таймаут) | Снова арендуется после окна батчинга |
| `msg.Postpone(ts)` | Нужно подождать перед повтором | Повтор после `ts` |
| `msg.Retry(ts, reason)` | Повтор с метаданными | Повтор с информацией о попытке |
| `msg.Aborted(reason)` | Намеренно пропустить | Отмечено как пропущенное, без повтора |
| `msg.ErrorMaxAttempts()` | Исчерпан максимум попыток | Запись в `__error$`, без повтора |

`Error`/`Error5xx` и `ErrorMaxAttempts` дополнительно передаются в `IPgOutboxPoisonHandler` до
записи статуса; см. [Обработчик «ядовитых» сообщений](Setup/Readme-ru.md#5-обработчик-ядовитых-сообщений-permanent-failures).

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
| **murmurHash3** | Компактная идентификация типов, кэшированная в `__type$` — см. [`TypeResolve/Readme-ru.md`](TypeResolve/Readme-ru.md) |
| **SqlCacheSplitter** | Дробит крупные многострочные INSERT/UPDATE на чанки до общей `DefaultMaxLen` (1024) — см. [`SqlBuilder/Readme-ru.md`](SqlBuilder/Readme-ru.md) и [`Commands/Readme-ru.md`](Commands/Readme-ru.md) |

---

## Массовая вставка

Правила публикации на больших объёмах и что мерить в проде — в отдельном документе:
[Bulk/Readme-ru.md](Bulk/Readme-ru.md).

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
