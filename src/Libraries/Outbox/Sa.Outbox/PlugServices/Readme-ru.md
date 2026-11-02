# PlugServices: точка подключения провайдера

## Зачем

`Sa.Outbox` не знает, где хранятся сообщения. Всё хранилище описано тремя контрактами:

| Контракт | Кто в ядре вызывает | Когда |
|---|---|---|
| `IOutboxBulkWriter` | `OutboxMessagePublisher` | на каждый publish |
| `IOutboxDeliveryManager` | `DeliveryTenant` | на каждый rent/return/extend |
| `IOutboxTenantDetector` | `TenantProvider` (опционален) | при автодетекте тенантов |

До этой спеки каждый пакет-провайдер регистрировал их вручную (`RemoveAll<T>().AddSingleton<T, Impl>()` в собственном `Setup`), а закомментированный `IOutboxPlugin` не давал ничего: в контейнере нельзя было узнать, какой провайдер установлен, нельзя было упасть на старте при неполной регистрации, а второй пакет провайдера молча перезаписывал первый.

Цель — **тонкий слой метаданных и регистрации**, без изменения горячего пути и публичных контрактов.

## Границы

Входит:

- идентичность провайдера (имя, версия) и декларация возможностей;
- единая регистрация трёх сервисов одним вызовом;
- fail-fast: дубликат провайдера и неполная регистрация обнаруживаются на старте, а не на первой публикации/тике;
- реестр для диагностики (`IOutboxProviderRegistry`).

Не входит (отдельные этапы):

- health-check и `InitializeAsync` (вариант B);
- несколько провайдеров в одном контейнере через keyed-регистрации (вариант C);
- изменение контрактов (`InsertBulk`, `Memory<IOutboxContextOperations<T>>`, `IOutboxContextFactory`) — только отдельным мажором.

## Публичный API

### Возможности

```csharp
[Flags]
public enum OutboxProviderCapabilities
{
    None = 0,
    PublishContext  = 1 << 0,  // InsertBulk(..., context) реально участвует в транзакции вызывающего
    TenantDetection = 1 << 1,  // есть IOutboxTenantDetector, CanDetect может быть true
    LockExtension   = 1 << 2,  // ExtendDelivery осмыслен (продление аренды)
    Postponement    = 1 << 3,  // Postpone/Retry с задержкой соблюдаются провайдером
}
```

Флаги — только декларация для диагностики и валидации. Ядро не меняет поведение по ним, кроме одной проверки: capability `TenantDetection` требует зарегистрированного детектора.

Известные расхождения, которые флаги делают явными: Redis не поддерживает publish-context и игнорирует задержку `Postpone/Retry`.

### Дескриптор

```csharp
public sealed record OutboxProviderDescriptor(
    string Name,
    OutboxProviderCapabilities Capabilities = OutboxProviderCapabilities.None,
    string? Version = null)
{
    public bool Supports(OutboxProviderCapabilities capability);
}
```

`Version` — для логов и диагностики. Если не задана явно, берётся из сборки bulk-writer'а.

### Регистрация

```csharp
public static IServiceCollection AddOutboxProvider(
    this IServiceCollection services,
    OutboxProviderDescriptor descriptor,
    Action<OutboxProviderBuilder> configure);

public sealed class OutboxProviderBuilder
{
    public OutboxProviderBuilder WithBulkWriter<T>()      where T : class, IOutboxBulkWriter;
    public OutboxProviderBuilder WithDeliveryManager<T>() where T : class, IOutboxDeliveryManager;
    public OutboxProviderBuilder WithTenantDetector<T>()  where T : class, IOutboxTenantDetector;
}
```

Пример (пакет провайдера, вызывается **до** `AddSaOutbox`, чтобы валидатор стартовал раньше расписаний):

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

`WithDeliveryManager` опционален: провайдер только для публикации его не вызывает, и это корректно, пока в приложении нет consumer-групп.

### Реестр

```csharp
public interface IOutboxProviderRegistry
{
    OutboxProviderDescriptor Descriptor { get; }
}
```

Синглтон, регистрируется `AddOutboxProvider`. Может использоваться в health/диагностике приложения.

## Правила валидации

| Проверка | Когда | Результат |
|---|---|---|
| `Name` не пуст | `AddOutboxProvider` | `ArgumentException` |
| Зарегистрирован bulk-writer (`WithBulkWriter`) | `AddOutboxProvider` | `InvalidOperationException` |
| Второй провайдер с **другим** `Name` | `AddOutboxProvider` | `InvalidOperationException` |
| Повторный `AddOutboxProvider` с тем же `Name` | `AddOutboxProvider` | идемпотентно: маркер заменяется |
| Провайдер вообще зарегистрирован | построение `IOutboxProviderRegistry` | `InvalidOperationException` |
| `IOutboxBulkWriter` **зарегистрирован** | построение реестра | `InvalidOperationException` |
| `IOutboxDeliveryManager` **зарегистрирован** при наличии групп | построение реестра | `InvalidOperationException` |
| Capability `TenantDetection` ⇒ детектор **зарегистрирован** | построение реестра | `InvalidOperationException` |
| `scopeKey` ядра и провайдера совпадают | — | **не проверяется** в этой версии (см. «Следующие шаги») |

Проверки построения реестра смотрят на **регистрации**, а не конструируют сервисы провайдера
(`IServiceProviderIsService`): построение storage-цепочки может открывать соединения и загружать
состояние, а реестр читается в том числе при сборке настроек delivery-job'ов.

Точки срабатывания:

1. `AddOutboxProvider` — статические проверки (имя, writer, дубликат).
2. `OutboxProviderValidationHostedService` — принудительно резолвит реестр на старте хоста и один раз пишет провайдера в лог.
3. Сборка настроек delivery-job'а — если реестр зарегистрирован, он резолвится и валидируется до первого тика. Это страхует случай, когда расписание зарегистрировано раньше валидатора.

## Поток исполнения

1. **Регистрация.** `AddSaOutboxUsingPostgreSql(...)` → провайдерные сервисы → `AddOutboxProvider(...)` → `AddSaOutbox(...)`. Порядок с ядром не важен: реестр строится лениво из `IEnumerable<OutboxProviderRegistration>`, дубликат `Name` ловится сразу.
2. **Старт.** Hosted-валидатор резолвит реестр: один провайдер, writer, delivery-manager при наличии групп, детектор при заявленной capability. Ошибка — падение старта с именем провайдера в тексте.
3. **Publish.** `IOutboxMessagePublisher` → `IOutboxBulkWriter`. `context` передаётся только если провайдер заявил `PublishContext` (проверка — на стороне вызывающего/документации; ядро по-прежнему пробрасывает его как есть).
4. **Delivery.** Тик `Sa.Schedule` → `DeliveryJob` → `DeliveryProcessor` → `TenantProvider` → `DeliveryTenant` → `IOutboxDeliveryManager` → `DeliveryCourier` → `ReturnDelivery`.
5. **Тенанты.** `AutoDetect` → `IOutboxTenantDetector`; иначе `TenantSettings`.
6. **Shutdown.** Дескриптор ресурсами не владеет; dispose — за DI и самими синглтонами.

## Совместимость

- Публичные контракты не меняются; поведение существующих приложений не меняется.
- Один провайдер на контейнер (как и раньше), но теперь это проверяется, а не «побеждает последний».
- `Sa.Outbox.PlugServices.IOutboxPlugin` (закомментированный) **удаляется**: его роль выполняют дескриптор + реестр, а сервисы остаются в DI.
- Keyed-мультипровайдерность и health — следующие этапы; дескриптор спроектирован так, чтобы `Name` мог стать `ServiceKey` без ломки API.

## Следующие шаги

- **B (health/lifecycle):** `IOutboxProviderHealth` с отчётом (healthy/reason/latency), адаптер под `Microsoft.Extensions.Diagnostics.HealthChecks`, опциональный `InitializeAsync` как провайдерский hosted-service.
- **C (keyed):** `scopeKey` в дескрипторе, keyed-регистрации всех storage-сервисов, `(key, group)` для консьюмеров.
- **Контракты:** убрать legacy-перегрузку `InsertBulk`, добавить opaque-токен аренды в `IOutboxContextFactory.Create`, типизировать publish-context.

## Тесты

- регистрация: writer/descriptor/version, отсутствие writer'а, два разных провайдера, повторная регистрация;
- реестр: без провайдера, без delivery-manager'а при наличии групп, publish-only без групп, `TenantDetection` без детектора;
- hosted-валидатор: присутствует и стартует;
- PG/Redis: имена и capability-флаги дескрипторов;
- существующие тесты PG/Redis/ядра остаются зелёными.

---

# Упрощение контрактов (мажор 0.18)

## Зачем

Текущие контракты несут два поколения API сразу:

- `IOutboxBulkWriter` — четыре перегрузки, из которых абстрактна **только legacy**; «простая»
  реализация тихо едет через аллоцирующий адаптер, а быстрый путь необязателен. `OutboxMessage<T>`
  (обёртка payload + part) живёт и в публикации, и внутри записи доставки.
- Аренда не имеет слота для собственной идентичности: Redis пакует stream id в `Guid OutboxId`,
  PostgreSQL читает `TaskId`/`DeliveryId` из `DeliveryInfo` — контракт молчит о том, что провайдеру
  нужно, чтобы вернуть сообщение в нужную строку.

Мажор убирает оба наслоения.

## Ломающие изменения

| Было | Стало |
|---|---|
| `InsertBulk(batch, ct)` + `InsertBulk(batch, context, ct)` + две legacy-перегрузки `InsertBulk(ReadOnlyMemory<OutboxMessage<T>>, …)` | один абстрактный `InsertBulk(batch, context, ct)` + convenience-перегрузка без контекста |
| `OutboxMessage<T>(PayloadId, Payload, PartInfo)` | удалён; батч публикации — `OutboxMessageBatchItem<T>` |
| `OutboxDeliveryMessage<T>(OutboxId, OutboxMessage<T> Message, DeliveryInfo)` | `OutboxDeliveryMessage<T>(OutboxId, PayloadId, Payload, PartInfo, DeliveryInfo, RentToken)` |
| арендная идентичность — неявно в `OutboxId` / `DeliveryInfo` | явный `OutboxRentToken(long Key, long SubKey)`, доступный и провайдеру (`IOutboxContext.RentToken`) |
| ошибка «контекст не поддерживается» — из провайдера в рантайме | `OutboxMessagePublisher` проверяет capability `PublishContext` до вызова writer'а |

## Публикация

```csharp
public interface IOutboxBulkWriter
{
    ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        object? context,
        CancellationToken cancellationToken = default);

    ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken = default)
            => InsertBulk(batch, context: null, cancellationToken);
}
```

- Один абстрактный метод: провайдер обязан реализовать именно быстрый batch-путь.
- `context` остаётся `object?` (типизированный контекст — отдельная тема), но при `context != null`
  и отсутствии capability `PublishContext` паблишер бросает `NotSupportedException` с именем
  провайдера **до** записи.
- `OutboxMessage<T>` удаляется: публикация использует `OutboxMessageBatchItem<T>`, доставка —
  плоскую запись.

## Доставка и rent-токен

```csharp
public readonly record struct OutboxRentToken(long Key, long SubKey = 0)
{
    public static OutboxRentToken None => default;
    public bool IsNone => Key == 0 && SubKey == 0;
}

public readonly record struct OutboxDeliveryMessage<TMessage>(
    Guid OutboxId,
    string PayloadId,
    TMessage Payload,
    OutboxPartInfo PartInfo,
    OutboxTaskDeliveryInfo DeliveryInfo,
    OutboxRentToken RentToken = default);
```

- Провайдер заполняет токен при аренде и читает его обратно из `IOutboxContext.RentToken`
  в `ReturnDelivery`/`ExtendDelivery` — без парсинга `OutboxId` и без побочных таблиц.
- Две 64-битные части — ровно то, чем адресуется строка: PostgreSQL `(task_id, delivery_id)`,
  Redis `(stream ms, seq)`. Ноль аллокаций.
- `IOutboxContextFactory.Create(delivery)` не меняется: токен путешествует внутри записи доставки.
- `OutboxId` остаётся публичной идентичностью сообщения провайдера (в Redis — упакованный stream id,
  как и раньше); токен — её арендная проекция.

## Миграция провайдеров

**PostgreSQL**

- `IBulkInsertMsgCommand`/`BulkInsertMsgCommand`: только batch-перегрузки (tx / no-tx); legacy
  `Execute(ReadOnlyMemory<OutboxMessage<T>>, …)`, `DistinctPartKeys` и legacy `WriteRows` удаляются —
  батч всегда один part, поэтому advisory-лок берётся по одному ключу.
- `StartDeliveryCommand`: плоская запись, `RentToken = (TaskId, DeliveryId)`.

**Redis**

- `OutboxBulkWriter`: две перегрузки (batch + context); legacy-ветка с группировкой по part удаляется.
- `RentDeliveryCommand`: плоская запись, `RentToken = (ms, seq)` из stream id.
- `FinishDeliveryCommand`: stream id восстанавливается из `context.RentToken`, а не из `OutboxId`;
  `RedisStreamId.ToStreamId(Guid)` становится ненужным.

## Версионирование

- `Sa.Outbox`, `Sa.Outbox.PostgreSql` → **0.18.0**; `Sa.Outbox.Redis` → **0.2.0** (провайдер ещё в разработке).
- Чек-лист для стороннего провайдера: реализовать `InsertBulk(batch, context, ct)`; перейти на
  плоскую `OutboxDeliveryMessage<T>`; заполнять `RentToken` при аренде и использовать его при
  возврате.

## Тесты

- ядро: единственный абстрактный метод writer'а; паблишер бросает `NotSupportedException` для
  контекста без capability; `RentToken` доходит до контекста и возвращается провайдеру;
- PG: batch-вставка (один part), токен `(task, delivery)` в возврате;
- Redis: токен `(ms, seq)`, `XACK` по токену, poison-путь без изменений;
- существующие интеграционные наборы PG/Redis остаются зелёными.
