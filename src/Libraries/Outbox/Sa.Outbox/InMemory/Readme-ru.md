# InMemory: эталонный провайдер

**Русский** | [English](Readme.md)

Реализация точки подключения outbox в памяти, живущая внутри пакета `Sa.Outbox`. Она совмещает две
роли:

- **замена хранилища для разработки и тестов** — никакой инфраструктуры, строк подключения и схемы,
  поэтому publish → rent → consume → acknowledge прогоняется без Docker;
- **эталонная реализация точки подключения**: провайдер занимает несколько сотен строк, читается
  за один заход и отвечает на вопрос «что вообще обязан делать провайдер».

Это намеренно **не** production-провайдер — см. [Ограничения](#ограничения). Про конвейер, в который
он встраивается, — [`../Delivery/Readme-ru.md`](../Delivery/Readme-ru.md); про сами контракты —
<<<<<<< HEAD
[`../PlugServices/Readme-ru.md`](../PlugServices/Readme-ru.md).
=======
[`../PlugServices/Readme-ru.md`](../PlugServices/Readme-ru.md). Чтобы сделать свой провайдер по этому
примеру, см. [`../PlugServices/ProviderGuide-ru.md`](../PlugServices/ProviderGuide-ru.md).
>>>>>>> sdd

## Содержание

- [Быстрый старт](#быстрый-старт)
<<<<<<< HEAD
=======
- [Пошаговая регистрация](#пошаговая-регистрация)
>>>>>>> sdd
- [Карта файлов](#карта-файлов)
- [Поведение](#поведение)
- [Отмена и ретраи](#отмена-и-ретраи)
- [Ограничения](#ограничения)

## Быстрый старт

```csharp
services.AddSaOutboxUsingInMemory(
    configure: settings => settings.Capacity = 1_000,   // 0 = без лимита (по умолчанию)
    configureOutbox: builder => builder
        .WithTenants((_, tenants) => tenants.WithTenantIds(1, 2))
        .WithDeliveries(deliveries => deliveries.AddDelivery<MyConsumer, MyMessage>("group")));
```

`AddSaOutboxUsingInMemory(configure, scopeKey)` — форма для одного провайдера; перегрузка с
`configureOutbox` регистрирует ещё и ядро. Отключённый провайдер (`Enabled = false`) не регистрирует
ничего: ни стора, ни контрактов, ни расписания.

<<<<<<< HEAD
=======
## Пошаговая регистрация

Рабочая сквозная версия этих шагов —
[`src/Samples/InMemoryOutbox.ConsoleApp`](../../../../Samples/InMemoryOutbox.ConsoleApp): она публикует
сообщения до старта хоста, а затем ждёт, пока потребитель подтвердит все.

### 1. Ставить нечего

Провайдер входит в пакет `Sa.Outbox` (namespace `Sa.Outbox.InMemory`), поэтому проекту, который уже
ссылается на `Sa.Outbox`, не нужны ни отдельный пакет, ни строка подключения, ни секция конфигурации.

### 2. Зарегистрировать провайдер

Один вызов регистрирует и провайдер, и ядро:

```csharp
builder.Services.AddSaOutboxUsingInMemory(
    configure: settings => settings.Capacity = 1_000,        // 0 = без лимита (по умолчанию)
    configureOutbox: outbox => outbox
        .WithTenants((_, tenants) => tenants.WithTenantIds(1, 2)));
```

Если ядро регистрируется в другом месте, используйте перегрузку только для провайдера и передайте
один и тот же scope key в оба вызова:

```csharp
builder.Services
    .AddSaOutboxUsingInMemory(scopeKey: MyScope)
    .AddSaOutbox(ConfigureOutbox, MyScope);
```

`settings.Enabled = false` превращает вызов в no-op — ни стора, ни контрактов, ни расписания; это
переключатель для случая, когда провайдер нужен не во всех окружениях.

> По умолчанию `Sa.Outbox` использует `JsonOutboxMessageSerializer` (reflection-JSON, не AOT-safe).
> В Native AOT приложении добавьте `.WithMessageSerializer(...)` с source-generated контекстом —
> ровно как в sample.

### 3. Написать потребителя

```csharp
public sealed class OrderConsumer(ILogger<OrderConsumer> logger) : IConsumer<OrderPlaced>
{
    public ValueTask Consume(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<OrderPlaced>> messages,
        CancellationToken cancellationToken)
    {
        foreach (IOutboxContextOperations<OrderPlaced> message in messages.Span)
        {
            logger.LogInformation("Заказ {PayloadId}, тенант {Tenant}", message.PayloadId, filter.TenantId);

            message.Ok();   // без этого сообщение в Pending всё равно считается доставленным
        }

        return ValueTask.CompletedTask;
    }
}
```

### 4. Зарегистрировать группу потребителей

```csharp
.WithDeliveries(deliveries => deliveries.AddDelivery<OrderConsumer, OrderPlaced>(
    "orders",                                   // id группы → DI-ключ и имя job-а
    (_, consumer) => consumer
        .WithInterval(TimeSpan.FromMilliseconds(200))
        .WithNoBatchingWindow()
        .StartImmediately()))
```

`AddDelivery` держит **один** экземпляр потребителя на процесс; `AddDeliveryScoped` создаёт свежий
DI-scope на каждый батч — это нужно потребителю с `DbContext` или зависимостью уровня запроса.
Каждая группа становится отдельным job-ом в собственном `Sa.Schedule` outbox-а.

### 5. Объявить тенантов и метаданные

```csharp
.WithTenants((_, tenants) => tenants.WithTenantIds(1, 2))
.WithMetadata((_, metadata) => metadata.AddMetadata<OrderPlaced>("orders", order => order.Id))
```

По умолчанию тенанты определяются автоматически: `InMemoryOutboxTenantDetector` отдаёт тенантов, у
которых ещё есть доставляемые сообщения, поэтому `WithTenantIds` нужен только чтобы зафиксировать
список. `AddMetadata` задаёт имя части (по умолчанию `"root"`) и payload id (по умолчанию —
`IOutboxPublishable.GetPayloadId()`).

### 6. Публиковать

```csharp
app.MapPost("/orders", async (OrderPlaced order, IOutboxMessagePublisher publisher, CancellationToken ct) =>
{
    await publisher.Publish([order], tenantId: 1, ct);
    return Results.Accepted();
});
```

`Publish` возвращает, сколько сообщений сохранено, и в сеть не ходит. Перегрузки с
`Func<TMessage, int>` сами группируют коллекцию по тенантам.

### 7. Запустить хост

```csharp
await host.StartAsync();
// … job доставки разбирает стор по своему расписанию …
await host.StopAsync();
```

Публиковать можно и до `StartAsync` (стор — это просто словарь), а вот **доставка не начнётся**:
расписание является `IHostedService`, именно оно и запускает потребителей.

### 8. Проверить, что регистрация сложилась

Health-проверки и журнала доставок нет, поэтому забытая регистрация выглядит как «ничего не
происходит». Смотрите в реестр, а не гадайте:

```csharp
IOutboxConsumerManager manager = app.Services.GetRequiredService<IOutboxConsumerManager>();
OutboxConsumerSettings? settings = manager.Get("orders");   // null → группа не зарегистрирована
```

Поведение наблюдайте логом внутри потребителя (шаг 3) или сигналом, как это делает sample.

### 9. Переключиться на реальный провайдер

Замените только вызов регистрации — потребители, тенанты, метаданные и вызовы `Publish` не зависят
от провайдера:

```csharp
builder.Services.AddSaOutboxUsingPostgreSql(pg => pg
    .WithDataSource(ds => ds.Options(o => o.Configure(c => c.ConnectionString = connectionString))));
```

Pending-сообщения между провайдерами **не** переносятся: перед переключением in-memory стор нужно
опустошить (drain).

>>>>>>> sdd
## Карта файлов

| Файл | Ответственность |
|---|---|
| `InMemoryOutboxStore.cs` | Всё хранилище: карта по id, упорядоченный индекс по `(tenant, part, payload type)`, лейзы и rent-поколение |
| `InMemoryOutboxBulkWriter.cs` | `IOutboxBulkWriter` — один метод: сериализовать батч и добавить его |
| `InMemoryOutboxDeliveryManager.cs` | `IOutboxDeliveryManager` — аренда записей, запись исходов, продление лейзов |
| `InMemoryOutboxTenantDetector.cs` | `IOutboxTenantDetector` — тенанты, у которых ещё есть доставляемые сообщения |
| `InMemoryOutboxRecord.cs` | Одно сообщение: payload плюс статус, счётчик попыток, лейз и владелец |
| `InMemoryOutboxSettings.cs` | `Enabled` и `Capacity`; намеренно больше ничего |
| `Setup.cs` | `AddSaOutboxUsingInMemory` — дескриптор, capabilities и три контракта |

## Поведение

| Аспект | Поведение |
|---|---|
| Хранилище | `Dictionary<long, record>` + упорядоченный индекс по `(tenant, part, payload type)` |
| Аренда | лейз внутри процесса (`LeaseExpiresAt`); истёкший лейз снова доступен |
| Задержка `Postpone`/`Retry` | соблюдается через `AvailableAt` |
| Счётчик попыток | растёт на `Retry`/`Warn`, не растёт на `Postpone` |
| Транзакция публикации | нет — non-null context бросает `NotSupportedException` |
| Ёмкость | `InMemoryOutboxSettings.Capacity`; переполнение бросает `OutboxException`, по умолчанию без лимита |
| Долговечность | **нет** — рестарт процесса теряет все сообщения |
| Межпроцессная доставка | **нет** — один экземпляр провайдера обслуживает один процесс |
| Health / прогрев | нет; отсутствие проверки считается «здоров» |
| Poison-политика | нет — статус и сообщение остаются в записи |
| Объявленные capabilities | `TenantDetection`, `LockExtension`, `Postponement` |

## Отмена и ретраи

Здесь вся история отмены укладывается в ~40 строк, поэтому этот провайдер — читаемый эталон для двух
реальных. Правила уровня конвейера (как остановка хоста отличается от собственного таймаута
потребителя, `PerTenantTimeout`, окно дублей at-least-once) описаны в
[`../Delivery/Readme-ru.md`](../Delivery/Readme-ru.md#отмена).

- **Публикация — «всё или ничего» на чанк.** `InMemoryOutboxBulkWriter` проверяет токен один раз,
  после проверки на пустоту; дальше `Insert` — одна мутация под локом стора. Токен, отменённый до
  этой проверки, не сохранит ничего; отменённый после — уже не наблюдается, потому что остаток вызова
  синхронный. Публикация больше `MaxBatchSize` всё равно режется на чанки ядром, поэтому отмена между
  чанками сохраняет уже вставленные.
- **Отменённый проход оставляет лейз, а не теряет сообщение.** `ReturnDelivery` не вызывается,
  поэтому записи остаются в `Processing` со своим `TransactId` и `LeaseExpiresAt`. Как только лейз
  истекает (`LockDuration`, по умолчанию 10 с), `Rent` снова считает их доступными.
- **Отмена не тратит попытку.** `Attempt` увеличивает только `Return`, и только для `Retry`/`Warn`,
  поэтому отменённый проход не трогает бюджет ретраев — в отличие от Redis, где счётчиком служит сама
  аренда.
- **Без дополнительного batching-окна.** Повторно арендованная запись и `Warn` с нулевой задержкой
  доступны сразу, как только это позволяет `LeaseExpiresAt`/`AvailableAt`; PostgreSQL дополнительно
  требует `lock_expires_on < now - BatchingWindow`. Собственный `CreatedAt` сообщения всё равно должен
  быть старше `ToDate = now - BatchingWindow`, иначе запись не выберется.
- **Устаревший исход не завершит чужую аренду.** `Return` отбрасывает исходы, у которых
  `RentGeneration` (вторая половина rent-токена) или `TransactId` уже не совпадают с записью, поэтому
  поздно вернувшийся проход — например, потребитель проглотил отмену — не сможет подтвердить
  сообщение, которое забрал другой проход.
- **Потеря лейза сообщается, а не скрывается.** `ExtendDelivery` возвращает `0`, если лейз истёк или
  перешёл другому; ядро считает это потерей блокировки и доставляет сообщение заново у нового
  владельца.

> Стор живёт в процессе: рестарт теряет сами сообщения, поэтому всё перечисленное не переживает
> процесс. В пределах его жизни отмена лишь сдвигает работу во времени.

## Ограничения

- **Нет долговечности.** Всё, включая `Pending` и незавершённые `Processing`, исчезает при выходе
  процесса.
- **Нет межпроцессной и межузловой доставки.** Один экземпляр провайдера обслуживает один процесс,
  поэтому в масштабируемом развёртывании он не заменяет общее хранилище.
- **Нет контекста публикации.** Входить не в чем, поэтому переданный context бросает
  `NotSupportedException`, а не игнорируется молча.
- **Нет сохранённых ошибок, poison-очереди, retention и trimming.** Итоговый статус и сообщение
  остаются в записи; `Capacity` — это fail-fast защита от неограниченного роста, а не retention.
- **Нет health-проверки, прогрева, миграций и партиционирования** — схемы нет и мигрировать нечего,
  а «здоровым» он отчитывается потому, что проверки просто нет.
- **Использует сериализатор ядра.** Оба контракта резолвят unkeyed `IOutboxMessageSerializer`,
  поэтому keyed-сериализатор конкретного провайдера здесь не применяется.

> In-memory провайдер **не** предназначен для production. Он нужен, чтобы прогонять пайплайн без
> базы и чтобы у автора провайдера был пример на несколько сотен строк вместо нескольких тысяч.
