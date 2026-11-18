# Sa.Outbox.Redis

**Русский** | [English](Readme.md)

Минимальная реализация Outbox-провайдера для `Sa.Outbox` поверх Redis Streams. Контракт
публикации и доставки тот же, что у `Sa.Outbox.PostgreSql`, но журнал сообщений, курсоры групп и
состояние повторов живут в Redis. Отдельного учёта у провайдера нет: неподтверждённая запись
остаётся в pending entries list (PEL) consumer group, и этот список вместе со счётчиком доставок
Redis и есть состояние повторов.

Без Lua: каждая операция — одна или несколько обычных команд Redis, независимые команды
отправляются пайплайном. Каждая команда работает с одним ключом, поэтому провайдер годится и для
Redis Cluster.

## Установка

```bash
dotnet add package Sa.Outbox.Redis
```

## Содержание

- [Установка](#установка)
- [Ключи](#ключи)
- [Быстрый старт](#быстрый-старт)
- [Подключение](#подключение)
- [Выключение провайдера](#выключение-провайдера)
- [Повторная регистрация того же ключа](#повторная-регистрация-того-же-ключа)
- [Публикация: алгоритм](#публикация-алгоритм)
- [Доставка: алгоритм](#доставка-алгоритм)
  - [Аренда: один опрос](#аренда-один-опрос)
  - [Завершение: фиксация исхода](#завершение-фиксация-исхода)
  - [Продление: удержание батча](#продление-удержание-батча)
- [Настройки](Configuration/Readme-ru.md)
  - [Обрезка](Configuration/Readme-ru.md#обрезка)
  - [«Ядовитые» сообщения](Configuration/Readme-ru.md#ядовитые-сообщения)
- [Несколько провайдеров в контейнере](#несколько-провайдеров-в-контейнере)
- [Миграция 0.1 → 0.2](#миграция-01--02)
- [Ограничения](#ограничения)

## Ключи

| Ключ | Тип | Назначение |
|---|---|---|
| `sa:outbox:stream:{tenant}:{part}:{type}` | stream | журнал сообщений одного `(tenant, part, тип payload)` |
| `sa:outbox:tenants` | set | id тенантов, когда-либо публиковавших |

Сама consumer group ключом не является: она живёт внутри stream, а её имя — это consumer group id,
переданный в `AddDelivery`/`AddDeliveryScoped`. Новая группа ничего не стоит: она читает тот же
журнал со своим курсором.

## Быстрый старт

```csharp
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.Publication;
using Sa.Outbox.Redis;

IHost host = Host.CreateDefaultBuilder()
    .ConfigureServices(services => services
        .AddSaOutbox(builder => builder
            .WithTenants((_, tenants) => tenants.WithTenantIds(1, 2))
            .WithDeliveries(deliveries => deliveries
                .AddDeliveryScoped<OrderConsumer, OrderCreated>((_, group) => group
                    .WithInterval(TimeSpan.FromSeconds(5))
                    .StartImmediately()
                    .WithMaxBatchSize(16)
                    .WithLockDuration(TimeSpan.FromSeconds(10)))))
        .AddSaOutboxUsingRedis(cfg => cfg
            .WithConnectionString("localhost:6379")))
    .Build();

var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();
await publisher.Publish([new OrderCreated("order-42")], tenantId: 1);

await host.RunAsync();
```

## Подключение

Соединение резолвится лениво, при первой команде, в порядке приоритета:

1. `WithDatabase(IDatabase)` / `WithDatabase(Func<IServiceProvider, IDatabase>)`
2. `WithConnectionMultiplexer(IConnectionMultiplexer)` или его фабрика
3. `IDatabase`, который приложение зарегистрировало в контейнере (до `AddSaOutboxUsingRedis`);
   собственный unkeyed-алиас default-провайдера не считается — keyed-провайдер его не занимает
4. `IConnectionMultiplexer`, уже зарегистрированный в контейнере
5. строка подключения: `WithConnectionString("…")` или `ConnectionStrings:Redis`

Мультиплексор создаёт и диспозит **только** путь со строкой подключения. Переданный приложением
(или зарегистрированный в DI) мультиплексор/база принадлежит вызывающему и никогда не диспозится
Outbox-ом — его можно делить с кэшем, SignalR и т.п.

## Выключение провайдера

`WithEnabled(false)` — **регистрационный** выключатель, а не runtime-флаг:

```csharp
services.AddSaOutboxUsingRedis(cfg => cfg
    .WithEnabled(configuration.GetValue("Outbox:Redis:Enabled", true))
    .WithConnectionString("localhost:6379"));
```

Колбэк конфигурации выполняется всегда, но в контейнер не попадает ничего: ни настройки, ни
соединение (мультиплексор, база или фабрика), ни переопределения сериализатора/poison, ни
consumer-группы, ни задачи `Sa.Schedule`. `Enabled` по умолчанию `true`; выключенный провайдер не
оставляет следа в DI, поэтому регистрацию можно держать в коде для всех окружений и гасить
конфигурацией. Провайдеры под другими ключами не затрагиваются. В раздельной форме (`AddSaOutbox` +
этот вызов) ядро всё равно регистрирует consumer-группы, поэтому выключенный провайдер уронит
стартовую проверку `scopeKey` с их именами в сообщении — не вызывайте `AddSaOutbox` или используйте
форму одного вызова.

## Повторная регистрация того же ключа

Повторный `AddSaOutboxUsingRedis(..., scopeKey)` для одного ключа допустим: контракты провайдера,
стартовый валидатор, layout guard, регистрации consumer-групп и расписание дедуплицируются.
Колбэки настроек outbox из последней регистрации заменяют ранее записанные, поэтому выполняются
один раз — а не по разу на регистрацию. Подключение принадлежит **первой** регистрации, поэтому
соединение настраивайте один раз.

## Публикация: алгоритм

```
Publish<TMessage>(messages, tenantId)
  1. метаданные: part (AddMetadata<TMessage>) + PayloadId + CreatedAt = TimeProvider.GetUtcNow()
  2. ключ stream = sa:outbox:stream:{tenantId}:{part}:{typeof(TMessage).Name}
  3. XADD каждого сообщения (один пайплайн IBatch, не MULTI/EXEC)
       i = PayloadId, t = typeof(TMessage).FullName, c = CreatedAt мс, p = сериализованный payload
  4. SADD sa:outbox:tenants {tenantId}
  5. обрезка retention (если включена): с троттлингом на stream, см. «Обрезка»
```

1. `IOutboxMessagePublisher` берёт part и payload id из метаданных, зарегистрированных через
   `AddMetadata<TMessage>`; `CreatedAt` — из `TimeProvider`.
2. Единица порядка — stream: один stream на `(tenant, part, тип payload)`. Identity типа — **простое**
   имя (`typeof(T).Name`, как `OutboxMessageFilter.PayloadType`). Два типа из разных namespace с
   одинаковым простым именем попадут в один stream — дайте им разные имена или разные part-ы.
3. Каждое сообщение батча — независимый `XADD`, отправленный пайплайном через `IBatch`. Транзакции
   нет: каждая запись атомарна сама по себе.
4. Тенант добавляется в set `tenants`, который читает `IOutboxTenantDetector`.
5. Если retention включён, stream обрезается сразу после батча с учётом троттлинга `TrimInterval`.
   Обрезка никогда не трогает pending- и ещё не доставленные записи.

Контекст публикации **не** поддерживается: `InsertBulk(..., context)` бросает
`NotSupportedException`, а не молча теряет транзакцию вызывающего. `Publish` возвращает число
добавленных записей.

## Доставка: алгоритм

```
Sa.Schedule job  (одна на consumer group; Interval / InitialDelay / IdleMaxInterval)
  └─ DeliveryJob<TMessage>
      └─ DeliveryProcessor        цикл: MaxProcessingIterations, IterationDelay
          ├─ SMEMBERS sa:outbox:tenants  → id тенантов
          └─ по тенантам (последовательно или PerTenantMaxDegreeOfParallelism)
              └─ DeliveryTenant.ProcessInTenant
                  ├─ фильтр: PayloadType=Name, TenantId, Part, ConsumerGroupId, TransactId,
                  │          FromDate = now - LookbackInterval, NowDate = now
                  ├─ размер батча = clamp(batcher.CalculateBatchSize, 0, MaxBatchSize)
                  ├─ RentDelivery            ← XAUTOCLAIM / XPENDING / XREADGROUP  (ниже)
                  ├─ LockRenewer             → ExtendDelivery каждые LockRenewal (XCLAIM JUSTID)
                  ├─ IConsumer<TMessage>.Consume(...)  → context.Ok()/Warn()/Retry()/Error()…
                  ├─ ReturnDelivery          → XACK | оставить pending | poison handler + XACK
                  └─ вернуть контексты в пул
```

1. Планировщик запускает один `DeliveryJob<TMessage>` на consumer group. Период — `Interval`,
   растягивается адаптивным backoff-ом на пустых очередях до `IdleMaxInterval`; `Paused` пропускает
   цикл.
2. `DeliveryProcessor` один раз за запуск снимает снапшот `OutboxConsumerSettings`, читает список
   тенантов и повторяет цикл, пока предыдущая итерация что-то обработала, но не более
   `MaxProcessingIterations` (с `IterationDelay` между итерациями). Тенанты обрабатываются
   последовательно или параллельно по `PerTenantMaxDegreeOfParallelism`.
3. `DeliveryTenant` строит `OutboxMessageFilter` (тип payload — то же простое имя, что и при
   публикации), запрашивает размер батча у `IDeliveryBatcher` и вызывает `RentDelivery`.
4. `RentDelivery` наполняет буфер (детали ниже). Всё, что он вернул, уже лежит в PEL группы под
   именем `TransactId` этого вызова.
5. Пока consumer работает, `LockRenewer` каждые `LockRenewal` вызывает `ExtendDelivery`, сбрасывая
   idle-таймер, чтобы долгий батч не увели.
6. `ReturnDelivery` применяет исход: успех → `XACK`, retryable → ничего (запись остаётся pending),
   permanent → poison handler + `XACK`.

### Аренда: один опрос

1. `XGROUP CREATE … MKSTREAM` при первом обращении (`BUSYGROUP` — норма), с позицией из
   `ConsumeSettings.StartFrom` (`Lookback` = `FromDate`, `Beginning` = `0-0`, `Now` = `$`). Позиция
   важна только при создании группы; позже её смена группу не двигает.
2. `XAUTOCLAIM <group> <consumer> <PendingClaimIdle> <cursor>` листает PEL страницами и забирает
   записи, простаивавшие не меньше `PendingClaimIdle` (по умолчанию 10 минут). `NextStartId`
   результата передаётся в следующий вызов, поэтому один опрос обходит весь PEL, не проверяя одни
   и те же записи повторно. Имя consumer-а — `TransactId` вызова.
3. `XPENDING` отдаёт счётчик доставок забранных id. Если он больше
   `ConsumeSettings.MaxDeliveryAttempts`, запись передаётся в `IRedisOutboxPoisonHandler` и
   подтверждается вместо повторной доставки — это защита от «ядовитых» сообщений.
4. Остаток батча добирается `XREADGROUP … >` — записи, которые ещё никто в группе не читал
   (`deliveryCount = 1`, `Attempt = 0`).
5. Каждая запись превращается в `IOutboxContextOperations<TMessage>`: `OutboxId` — stream id,
   упакованный в `Guid`, `TaskId` выводится из той же пары, `Attempt = deliveryCount - 1`, payload
   десериализуется через core-сериализатор `IOutboxMessageSerializer` (`Sa.Outbox.Serialization`; по умолчанию — UTF-8 JSON).

### Завершение: фиксация исхода

- успех (`Ok`, `Created`, `301`, …) → `XACK`;
- `Postpone`/`Retry`/`Warn`/`Pending` → команд нет: запись остаётся pending и будет забрана снова
  после `PendingClaimIdle`;
- `Error`/`Error5xx`/`MaximumAttemptsError` → poison handler, затем `XACK`.

`Postpone`/`Retry` здесь не несут срок: в stream-only модели нет таймера на запись, поэтому
гранулярность повтора — `PendingClaimIdle`.

### Продление: удержание батча

`XCLAIM <stream> <group> <consumer> 0 <id> JUSTID` для pending-записей вызова: сбрасывает idle, не
увеличивая счётчик доставок.

`DeliveryInfo.Attempt` — это счётчик доставок pending-записи минус один: `0` при первой аренде, `1`
после первого повторного захвата и т.д. Провайдер передаёт запись в poison handler, когда счётчик
доставок превысил `ConsumeSettings.MaxDeliveryAttempts`; потребительский `MaxDeliveryAttempts`
по-прежнему действует на исходы, вернувшиеся через `ReturnDelivery`, — как и в PostgreSQL-провайдере.

`OutboxId` — это stream id, упакованный в 16 байт `Guid` (8 байт миллисекунд + 8 байт
последовательности, big-endian); `TaskId` выводится из той же пары. Индекса Guid → stream id нет —
бизнес-ключ это `PayloadId`.

## Настройки

Слои настроек, ключи, обрезка и «ядовитые» сообщения — в отдельном документе:
[Configuration/Readme-ru.md](Configuration/Readme-ru.md).

## Несколько провайдеров в контейнере

`AddSaOutboxUsingRedis` принимает провайдерский ключ (он же `scopeKey`), поэтому в одном
контейнере можно держать несколько Redis-outbox'ов — со своим подключением, настройками, расписанием
и consumer-группами:

```csharp
services
    .AddSaOutboxUsingRedis(
        "cache",                                     // provider key
        r => r.WithConnectionString("cache"),
        outbox => outbox.WithDeliveries(d => d.AddDelivery<CacheConsumer, CacheEvent>("cache_events")))
    .AddSaOutboxUsingRedis(
        "billing",
        r => r.WithConnectionString("billing"),
        outbox => outbox.WithDeliveries(d => d.AddDelivery<InvoiceConsumer, Invoice>("invoices")));

var cache = sp.GetRequiredService<IOutboxPublisherResolver>().GetPublisher("cache");
```

- Настройки, named options, `IDatabase` и все команды — per provider; `WithOutboxSettings` /
  `WithConfiguration` применяются только к своему ключу. `WithMessageSerializer` тоже keyed: влияет
  только на этот провайдер, а провайдер без своего сериализатора следует общему уровня контейнера
  (последний fallback — core JSON).
- Если провайдер на не-дефолтном ключе не задал свой префикс, ключи автоматически получают суффикс
  `:{providerKey}` (`sa:outbox:cache:...`) — два провайдера на одной Redis БД не пересекаются.
- Без ключа (`AddSaOutboxUsingRedis(cfg)`) всё работает как раньше: default key `outbox`, префикс
  `sa:outbox`, unkeyed-алиасы `IDatabase`/настроек/паблишера.
- Стартовая проверка `RedisMultiProviderGuard` падает, если два провайдера смотрят в один endpoint
  и базу с одинаковым префиксом (иначе каждое сообщение доставлялось бы дважды); явная фабрика
  `IDatabase`/`IConnectionMultiplexer` не проверяется — её endpoint неизвестен до запуска.
- Тенанты — per provider (`TenantSettings` keyed; настройки дефолтного провайдера служат общим
  fallback), а id consumer-групп могут совпадать (композитные ключи).
## Миграция 0.1 → 0.2

- **Данные в Redis не мигрируют.** Default key (`AddSaOutboxUsingRedis(cfg)`) использует прежний
  префикс `sa:outbox`; стримы, группы и PEL читаются как раньше.
- **Не-дефолтный provider key** получает суффикс `:{key}` (`sa:outbox:billing:...`), поэтому
  провайдеры на одной БД никогда не делят стримы. Суффикс добавляется после `WithOutboxSettings` и
  `WithConfiguration`; чтобы закрепить точный префикс, добавьте действие `Options`, которое
  выполняется последним: `Options(o => o.Configure(s => s.KeySettings.Prefix = "sa:outbox"))`.
- **Регистрация**: односрочная форма `AddSaOutboxUsingRedis(cfg, outbox, key)`; настройки и команды
  keyed, для default key сохранены unkeyed-алиасы (`IDatabase`, `RedisOutboxSettings`, паблишер).
- **Контракты 0.18**: у `IOutboxBulkWriter` один batch-метод, `OutboxDeliveryMessage<T>` — плоская
  запись с `RentToken` (см. [`Sa.Outbox/PlugServices/Readme-ru.md`](../Sa.Outbox/PlugServices/Readme-ru.md)).


## Ограничения

- Обрезка встроена в публикацию и работает по stream; stream, который перестал получать публикации,
  не обрезается (он и не растёт). Обрезка не удаляет записи, нужные группе, но не заменяет
  `maxmemory` как жёсткий верхний предел.

- Нет контекста/транзакции публикации (см. выше).
- Тайминг повторов грубый: упавшее или отложенное сообщение доставляется повторно через
  `PendingClaimIdle`, а не через задержку, переданную в `Postpone`/`Retry`. Каждый захват также
  расходует бюджет `MaxDeliveryAttempts` провайдера, включая отложенные сообщения.
- Доставка at-least-once: запись, простаивавшая дольше `PendingClaimIdle` во время выполнения батча,
  будет доставлена повторно. Consumer-ы должны быть идемпотентны.
- Порядок — в пределах stream, то есть `(tenant, part, тип payload)`, не глобальный.
