# Sa.Outbox.Redis

Минимальная реализация Outbox-провайдера для `Sa.Outbox` поверх Redis Streams. Контракт
публикации и доставки тот же, что у `Sa.Outbox.PostgreSql`, но журнал сообщений, курсоры групп и
состояние повторов живут в Redis. Отдельного учёта у провайдера нет: неподтверждённая запись
остаётся в pending entries list (PEL) consumer group, и этот список вместе со счётчиком доставок
Redis и есть состояние повторов.

Без Lua: каждая операция — одна или несколько обычных команд Redis, независимые команды
отправляются пайплайном. Каждая команда работает с одним ключом, поэтому провайдер годится и для
Redis Cluster.

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
3. `IDatabase`, уже зарегистрированный в контейнере (до `AddSaOutboxUsingRedis`)
4. `IConnectionMultiplexer`, уже зарегистрированный в контейнере
5. строка подключения: `WithConnectionString("…")` или `ConnectionStrings:Redis`

Мультиплексор создаёт и диспозит **только** путь со строкой подключения. Переданный приложением
(или зарегистрированный в DI) мультиплексор/база принадлежит вызывающему и никогда не диспозится
Outbox-ом — его можно делить с кэшем, SignalR и т.п.

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
   десериализуется через `IOutboxMessageSerializer`.

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

Объект настроек — обычный экземпляр `Microsoft.Extensions.Options`. Слои применяются по порядку,
каждый перекрывает предыдущий:

1. значения по умолчанию из кода в `WithOutboxSettings`;
2. секция конфигурации, переданная в `WithConfiguration`;
3. действия `Options(...)` — прямой доступ к конвейеру.

Валидация выполняется при первом разрешении настроек и при старте хоста, если контейнер проверяет
options (`ValidateOnStart`). Некорректное значение проявляется как `OptionsValidationException`.

```csharp
cfg.WithConfiguration("Sa:Outbox:Redis");

cfg.WithOutboxSettings((sp, settings) =>
{
    settings.KeySettings.Prefix = "sa:outbox";
    settings.KeySettings.Separator = ":";

    settings.ConsumeSettings.StartFrom = RedisOutboxGroupStart.Lookback; // Beginning | Now
    settings.ConsumeSettings.PendingClaimIdle = TimeSpan.FromMinutes(10);
    settings.ConsumeSettings.MaxDeliveryAttempts = 5;
});

// Выход за оба слоя: последнее слово на стандартном конвейере options.
cfg.Options(options => options.Configure(settings => settings.RetentionSettings.Enabled = true));
```

```json
{
  "Sa": {
    "Outbox": {
      "Redis": {
        "KeySettings": { "Prefix": "sa:outbox", "Separator": ":" },
        "ConsumeSettings": {
          "StartFrom": "Lookback",
          "PendingClaimIdle": "00:10:00",
          "MaxDeliveryAttempts": 5
        },
        "RetentionSettings": {
          "Enabled": true,
          "StreamMaxAge": "7.00:00:00",
          "TrimInterval": "00:05:00",
          "Approximate": true,
          "TrimLimit": 10000
        }
      }
    }
  }
}
```

`StartFrom` важен только при создании группы на существующем stream: `Lookback` — от
`OutboxMessageFilter.FromDate` (окно lookback группы), `Beginning` — с `0-0`, `Now` — с `$`.

`PendingClaimIdle` — единственные часы повторов: запись, которая не была подтверждена (ошибка,
отложена, упавший воркер), снова станет доступной после того, как простаивала столько времени.
Держите значение заметно больше `LockDuration` consumer-а. `MaxDeliveryAttempts` ограничивает,
сколько раз такую запись можно забрать, прежде чем передать её в poison handler.

### Обрезка

Автоматическая обрезка по умолчанию **выключена**. Если задать `Enabled`, она выполняется прямо
после батча публикации с троттлингом на stream по `TrimInterval` (5 минут по умолчанию): stream,
который перестал расти, просто не обрезается.

Stream сообщений никогда не режется по голой длине. Провайдер вычисляет безопасную границу по
группам этого stream (`XINFO GROUPS`, а при наличии pending — ещё и `XPENDING`):

- группа с pending-записями даёт свой самый старый pending id;
- без pending — свой `last-delivered-id` (всё до него доставлено и подтверждено);
- группа, которая ещё ничего не прочитала (`last-delivered-id = 0-0`), блокирует обрезку: она может
  начать с начала.

Граница — минимум по всем группам, а удаляется диапазон `min(safeFloor, now - StreamMaxAge)`:
запись должна быть старше окна хранения **и** не нужна ни одной группе. `StreamMaxAge = 0` отключает
возрастной предел, но сохраняет безопасную границу. Stream без групп не обрезается вовсе, чтобы
группа, созданная позже с `StartFrom = Beginning`, увидела полный лог.

`Approximate = true` использует `XTRIM ... ~`: это заметно дешевле для Redis и может оставить чуть
больше записей, чем задано. `TrimLimit` соответствует `XTRIM ... LIMIT` и требует приблизительного
режима (ненулевой `TrimLimit` при `Approximate = false` не проходит валидацию); `0` снимает предел.

### «Ядовитые» сообщения

Dead-letter stream нет. Когда провайдер перестаёт доставлять сообщение — счётчик доставок в PEL
превысил `ConsumeSettings.MaxDeliveryAttempts` или consumer вернул постоянную ошибку — он вызывает
`IRedisOutboxPoisonHandler` и, если обработчик завершился успешно, подтверждает запись (`XACK`).

Стандартный обработчик пишет Warning и выбрасывает сообщение. Чтобы сохранять его самому, замените
обработчик:

```csharp
cfg.WithPoisonHandler<MyPoisonHandler>();          // из DI
cfg.WithPoisonHandler(new MyPoisonHandler());      // экземпляр
cfg.WithPoisonHandler(sp => new MyPoisonHandler(sp.GetRequiredService<MyStore>()));
```

```csharp
public sealed class MyPoisonHandler(MyStore store) : IRedisOutboxPoisonHandler
{
    public ValueTask Handle(RedisOutboxPoisonMessage message, CancellationToken cancellationToken)
        => store.Save(message.PayloadId, message.PayloadType, message.StatusCode,
                      message.Exception, message.Payload, cancellationToken);
}
```

Обработчик вызывается **до** `XACK`. Успешный возврат означает «можно подтверждать»; исключение
оставляет запись pending, и она будет забрана снова — то есть обработка at-least-once, дубликаты
нужно терпеть. Сама запись остаётся в stream до обрезки retention, поэтому даже выброшенное
сообщение ещё какое-то время можно посмотреть через `XRANGE`.

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
