# Как подключить свой провайдер

**Русский** | [English](ProviderGuide.md)

Пошаговое руководство по подключению собственного хранилища к `Sa.Outbox` на примере in-memory
провайдера, который поставляется в этом же пакете. Эталонные исходники —
[`../InMemory/`](../InMemory/); в руководстве они приведены в сокращённом виде, чтобы можно было
читать их параллельно.

Сами контракты описаны в [`Readme-ru.md`](Readme-ru.md); этот файл — про то, как их реализовать.

## Содержание

- [Что вы пишете и что получаете](#что-вы-пишете-и-что-получаете)
- [1. Форма провайдера](#1-форма-провайдера)
- [2. Состояние: одна запись, один индекс](#2-состояние-одна-запись-один-индекс)
- [3. `IOutboxBulkWriter` — сохранение батча](#3-ioutboxbulkwriter--сохранение-батча)
- [4. `IOutboxDeliveryManager` — аренда, возврат, продление](#4-ioutboxdeliverymanager--аренда-возврат-продление)
- [5. `IOutboxTenantDetector` — необязательный поиск тенантов](#5-ioutboxtenantdetector--необязательный-поиск-тенантов)
- [6. Регистрация](#6-регистрация)
- [7. Что ядро делает за вас](#7-что-ядро-делает-за-вас)
- [8. Чек-лист контракта](#8-чек-лист-контракта)
- [9. Тестирование провайдера](#9-тестирование-провайдера)
- [10. Capabilities и несколько провайдеров](#10-capabilities-и-несколько-провайдеров)

## Что вы пишете и что получаете

| Вы реализуете | Ядро даёт вам |
|---|---|
| `IOutboxBulkWriter` — сохранить один батч | нарезку по `MaxBatchSize`, получение payload id, весь publish-API |
| `IOutboxDeliveryManager` — арендовать батч, записать исход, продлить лейз | батчинг, обход тенантов, цикл продления лейза, отмену, политику ретраев/backoff, `MaxDeliveryAttempts` → `508`, семантику статусов, расписание |
| `IOutboxTenantDetector` — необязательно | разветвление по тенантам |

Провайдер — это **только хранилище**. Ничего про ретраи, статусы или потребителей в нём быть не
должно — см. [§7](#7-что-ядро-делает-за-вас).

## 1. Форма провайдера

Достаточно четырёх файлов; у in-memory провайдера ровно такая раскладка:

```
MyProvider/
├── MyOutboxStore.cs              состояние: записи, индекс, лейзы
├── MyOutboxBulkWriter.cs        IOutboxBulkWriter
├── MyOutboxDeliveryManager.cs    IOutboxDeliveryManager
├── MyOutboxTenantDetector.cs    IOutboxTenantDetector (необязательно)
└── Setup.cs                     AddOutboxProvider(...)
```

Провайдер может жить в сборке приложения; отдельный NuGet-пакет нужен только если им будут
пользоваться другие. Всё необходимое публично: контракты, `OutboxMessageBatch`,
`OutboxDeliveryMessage`, `OutboxRentToken`, `IOutboxContextFactory`, `OutboxPayloadCodec`,
`OutboxProviderDescriptor`.

## 2. Состояние: одна запись, один индекс

Запись in-memory провайдера — та форма, к которой приходит любой провайдер: сообщение, состояние
доставки, лейз и владелец в одной строке:

```csharp
internal sealed class MyOutboxRecord
{
    public long Id { get; set; }                        // ваш id строки → RentToken.Key
    public Guid OutboxId { get; init; }                 // публичная идентичность
    public int TenantId { get; init; }
    public string Part { get; init; } = "";             // часть сообщения (метаданные)
    public string PayloadType { get; init; } = "";      // Type.Name, по нему матчит фильтр
    public string PayloadId { get; init; } = "";        // ключ приложения
    public byte[] Payload { get; init; } = [];          // сериализованное тело
    public DateTimeOffset CreatedAt { get; init; }

    public DeliveryStatusCode Status { get; set; } = DeliveryStatusCode.Pending;
    public int Attempt { get; set; }
    public DateTimeOffset? AvailableAt { get; set; }     // после Postpone/Retry/Warn
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public long RentGeneration { get; set; }             // RentToken.SubKey
    public string? TransactId { get; set; }              // текущий владелец
}
```

> Настоящее хранилище обычно разделяет это на таблицу сообщений и таблицу задач на каждую группу
> потребителей (так делает PostgreSQL); простое хранилище может держать одну строку на сообщение и
> обслуживать из неё все группы.

## 3. `IOutboxBulkWriter` — сохранение батча

Единственный обязательный контракт. У батча один `PartInfo` (тенант, часть, время), поэтому
разрешение делается один раз:

```csharp
internal sealed class MyOutboxBulkWriter(MyOutboxStore store, IOutboxMessageSerializer serializer)
    : IOutboxBulkWriter
{
    public ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        object? context,
        CancellationToken cancellationToken = default)
    {
        // Входить не в чем. Лучше отказать, чем молча проигнорировать транзакционную
        // гарантию, которую ожидает вызывающий; PublishContext объявляйте, только если соблюдаете.
        if (context is not null)
            throw new NotSupportedException("This provider has no publish context.");

        if (batch.IsEmpty) return ValueTask.FromResult(0UL);

        // Проверяйте токен до первой записи: провайдер, чьи команды нельзя отменить, иначе
        // сохранит батч, от которого вызывающий уже отказался.
        cancellationToken.ThrowIfCancellationRequested();

        List<MyOutboxRecord> records = new(batch.Count);

        foreach (OutboxMessageBatchItem<TMessage> item in batch.Messages.Span)
        {
            records.Add(new MyOutboxRecord
            {
                TenantId = batch.PartInfo.TenantId,
                Part = batch.PartInfo.Part,
                PayloadType = typeof(TMessage).Name,
                PayloadId = item.PayloadId,
                Payload = OutboxPayloadCodec.Serialize(serializer, item.Payload),
                CreatedAt = batch.PartInfo.CreatedAt,
            });
        }

        return ValueTask.FromResult(store.Insert(records));   // ulong: сколько сохранено
    }
}
```

Правила:

- возвращайте число реально сохранённых сообщений (`ulong`), не байты и не «1»;
- сохраняйте батч целиком или никак, если хранилище это позволяет — ядро режет публикацию на чанки
  и считает чанк единым целым;
- `PayloadType` обязан быть `typeof(TMessage).Name` (именно это несёт
  `OutboxMessageFilter.PayloadType`);
- `OutboxPayloadCodec` — удобство (пул-стрим → массив точного размера). Писать сразу в свой writer
  тоже можно и даже дешевле (без копии).

## 4. `IOutboxDeliveryManager` — аренда, возврат, продление

Единственный контракт с семантикой, на которую опирается ядро. Реализация в памяти достаточно
коротка, чтобы читать её целиком:

```csharp
internal sealed class MyOutboxDeliveryManager(
    MyOutboxStore store,
    IOutboxContextFactory contextFactory,
    IOutboxMessageSerializer serializer) : IOutboxDeliveryManager
{
    public Task<int> RentDelivery<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        // Отменённый цикл не должен открывать лейз вообще.
        if (cancellationToken.IsCancellationRequested || writeBuffer.IsEmpty)
            return Task.FromResult(0);

        List<MyOutboxRecord> rented = store.Rent(lockDuration, filter, writeBuffer.Length);

        for (int i = 0; i < rented.Count; i++)
        {
            writeBuffer.Span[i] = contextFactory.Create<TMessage>(
                new OutboxDeliveryMessage<TMessage>(
                    OutboxId: rented[i].OutboxId,
                    PayloadId: rented[i].PayloadId,
                    Payload: OutboxPayloadCodec.Deserialize<TMessage>(serializer, rented[i].Payload)!,
                    PartInfo: new OutboxPartInfo(rented[i].TenantId, rented[i].Part, rented[i].CreatedAt),
                    // Часть задачи — это (тенант, группа потребителей, время задачи), не часть сообщения.
                    DeliveryInfo: new OutboxTaskDeliveryInfo(
                        TaskId: rented[i].Id,
                        DeliveryId: rented[i].RentGeneration,
                        Attempt: rented[i].Attempt,
                        LastErrorId: 0,
                        Status: default,
                        PartInfo: new OutboxPartInfo(rented[i].TenantId, filter.ConsumerGroupId, filter.NowDate)),
                    RentToken: new OutboxRentToken(rented[i].Id, rented[i].RentGeneration)));
        }

        return Task.FromResult(rented.Count);
    }

    public Task<int> ReturnDelivery<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        List<MyOutboxOutcome> outcomes = new(messages.Length);

        foreach (IOutboxContextOperations<TMessage> context in messages.Span)
        {
            if (context.RentToken.IsNone) continue;

            outcomes.Add(new MyOutboxOutcome(
                context.RentToken.Key, context.RentToken.SubKey,
                context.DeliveryResult, context.PostponeDelay));
        }

        return Task.FromResult(store.Return(outcomes, filter));
    }

    public Task<int> ExtendDelivery(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => Task.FromResult(store.Extend(lockExpiration, filter));
}
```

с одним небольшим типом на один исход:

```csharp
internal readonly record struct MyOutboxOutcome(
    long Id, long Generation, DeliveryStatus Status, TimeSpan Delay);
```

`Generation` — то, что делает проверяемым правило №6 из [§8](#8-чек-лист-контракта): исход, чьё
поколение больше не совпадает с записью, относится к аренде, которую уже забрали.

Что обязан гарантировать стор:

| Вызов | Обязан |
|---|---|
| `RentDelivery` | Выбрать сообщения **`filter.ConsumerGroupId` + `filter.TenantId` + `filter.Part` + `filter.PayloadType`**, которые арендуемы, пометить их владением с дедлайном, вернуть количество и заполнить буфер в том же порядке |
| `RentDelivery` | Считать `Processing` с истёкшим лейзом арендуемым — именно так восстанавливается упавший или отменённый проход |
| `ReturnDelivery` | Записать вердикт потребителя из `context.DeliveryResult`, увеличить `Attempt` для `Retry`/`Warn` (**не** для `Postpone`), выставить время повторной аренды из `context.PostponeDelay`, вернуть число применённых исходов |
| `ReturnDelivery` | Игнорировать исход с устаревшей арендой (сообщение уже переарендовано): сравнивать `context.RentToken` с текущим владельцем/поколением, а не перезаписывать |
| `ExtendDelivery` | Продлить лейз сообщений, принадлежащих `filter.TransactId`, и вернуть **сколько реально продлено**; `0` означает, что аренда потеряна, и ядро останавливает батч |

Держите rent-токен стабильным и непрозрачным: это единственное, что возвращается в return/extend.
In-memory использует `(id строки, поколение аренды)`, PostgreSQL — `(task id, delivery id)`, Redis
пакует id стрима.

## 5. `IOutboxTenantDetector` — необязательный поиск тенантов

Нужен, только если вы объявляете `OutboxProviderCapabilities.TenantDetection` (или если приложение
не перечисляет тенантов явно):

```csharp
internal sealed class MyOutboxTenantDetector(MyOutboxStore store) : IOutboxTenantDetector
{
    public bool CanDetect => true;

    public ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(store.GetTenantIds());   // тенанты, у которых есть работа
    }
}
```

Возвращайте только тенантов, у которых ещё есть работа: цикл обходит каждый полученный id, и id,
который никогда ничего не отдаёт, стоит по одному `RentDelivery` на проход.

## 6. Регистрация

Оберните `AddOutboxProvider` в привычную форму `AddSaOutboxUsing...`:

```csharp
public static IServiceCollection AddSaOutboxUsingMyStore(
    this IServiceCollection services,
    Action<MyOutboxOptions>? configure = null,
    Action<IOutboxBuilder>? configureOutbox = null,
    string scopeKey = OutboxScope.DefaultKey)
{
    OutboxScope.EnsureValid(scopeKey);

    MyOutboxOptions options = new();
    configure?.Invoke(options);
    options.Validate();

    // Публикатору ядра нужен TimeProvider, а ядро его не регистрирует.
    services.TryAddSingleton(TimeProvider.System);

    services.TryAddKeyedSingleton(scopeKey, (sp, _) => new MyOutboxStore(
        sp.GetRequiredService<TimeProvider>(), options));

    services.AddSaOutbox(configureOutbox, scopeKey);

    return services.AddOutboxProvider(
        scopeKey,
        new OutboxProviderDescriptor(
            "MyStore",                                             // стабильное, человекочитаемое
            OutboxProviderCapabilities.TenantDetection
            | OutboxProviderCapabilities.LockExtension
            | OutboxProviderCapabilities.Postponement),
        provider => provider
            .WithBulkWriter(sp => new MyOutboxBulkWriter(
                sp.GetRequiredKeyedService<MyOutboxStore>(scopeKey),
                sp.GetRequiredService<IOutboxMessageSerializer>()))
            .WithDeliveryManager(sp => new MyOutboxDeliveryManager(
                sp.GetRequiredKeyedService<MyOutboxStore>(scopeKey),
                sp.GetRequiredService<IOutboxContextFactory>(),
                sp.GetRequiredService<IOutboxMessageSerializer>()))
            .WithTenantDetector(sp => new MyOutboxTenantDetector(
                sp.GetRequiredKeyedService<MyOutboxStore>(scopeKey))));
}
```

Замечания:

- **Фабрики, а не типы.** Keyed-регистрация конкретного типа резолвит параметры конструктора
  *unkeyed*, поэтому сервисы провайдера регистрируются фабриками, которые сами достают свои
  keyed-зависимости.
- **`scopeKey` — это ключ провайдера.** Он должен совпадать с `scopeKey`, переданным в
  `AddSaOutbox`; конвейер доставки носит его как `OutboxConsumerSettings.ProviderKey`.
- **`WithBulkWriter` обязателен** — без него `AddOutboxProvider` бросает исключение.
  `WithDeliveryManager` обязателен, как только приложение регистрирует группы потребителей;
  `WithTenantDetector` обязателен, если объявлен `TenantDetection`. Проверки выполняются на старте
  хоста (`OutboxProviderValidationHostedService`), поэтому недостающий кусок ломает запуск, а не
  первое сообщение.
- Отключённый провайдер должен не регистрировать **ничего** и выходить сразу — так делают оба
  поставляемых провайдера при `Enabled = false`.

## 7. Что ядро делает за вас

**Не** реализуйте это в провайдере:

| Поведение | Кто отвечает |
|---|---|
| Нарезка публикации по `MaxBatchSize`, получение payload id из метаданных | публикатор (`OutboxMessagePublisher`) |
| Обход тенантов, размер батча, бюджет итераций, пауза | цикл доставки (`DeliveryProcessor`) |
| Вызов потребителя, превращение throw в `Warn` + backoff, обработка отмены | курьер (`DeliveryCourier`) |
| `Attempt + 1 > MaxDeliveryAttempts` → `MaximumAttemptsError` (508) | политика post-handle курьера |
| Продление лейза, пока работает потребитель, и падение на `renewed == 0` | проход по тенанту + реньювер ядра |
| Расчёт задержки `Retry`/`Postpone` для сбоев | `IDeliveryBackoffStrategy` |
| Расписание, адаптивный idle, конкурентность | `Sa.Schedule` |

Ваша задача — одна фраза: **сделать rent/return/extend долговечными и корректными при
конкурентном доступе**.

## 8. Чек-лист контракта

| # | Правило | Зачем |
|---|---|---|
| 1 | `RentDelivery` возвращает `0`, если токен уже отменён | отменённый проход не должен создавать лейз, который никогда не вернёт |
| 2 | Сообщение снова арендуемо после падения или отмены, как только истёк лейз | восстановление at-least-once |
| 3 | `ReturnDelivery` увеличивает `Attempt` только для `Retry`/`Warn` | `Postpone` управляется потребителем и не должен исчерпывать `MaxDeliveryAttempts` |
| 4 | Терминальные статусы (2xx, 5xx, `508`) выводят из арендуемого набора; `Postpone`/`Retry`/`Warn` возвращают в него в `now + PostponeDelay` | шкала статусов — контракт ядра |
| 5 | `ExtendDelivery` возвращает реально продлённое число; `0` означает потерю аренды | ядро поднимает `LockRenewalException` и останавливает батч |
| 6 | Устаревший исход никогда не перезаписывает более новую аренду | иначе поздний воркер подтвердит чужой батч |
| 7 | Rent-токен стабилен и позволяет снова найти сообщение | это единственная возвращаемая вам идентичность |
| 8 | Публикация проверяет отмену до первой записи и возвращает число сохранённых | вызывающий не должен получить успех за работу, которой не было |
| 9 | `PayloadType` — это `typeof(TMessage).Name` | фильтр матчит ровно по нему |
| 10 | Никакой дедупликации между сообщениями не делается | outbox по дизайну at-least-once |

## 9. Тестирование провайдера

Два уровня, оба дешёвые:

1. **Уровень контракта, без хоста:** соберите контейнер с `AddSaOutboxUsingMyStore()`, достаньте
   `IOutboxBulkWriter` / `IOutboxDeliveryManager` по ключу scope и дёргайте их напрямую — публикация,
   аренда, возврат, продление, переаренда. Именно это делает
   [`../../../../Tests/Sa.Outbox.Tests/InMemory/`](../../../../Tests/Sa.Outbox.Tests/InMemory/): прогон
   занимает миллисекунды и покрывает каждое правило из §8.
2. **Сквозной:** поднимите настоящий `IHost`, опубликуйте и дайте расписанию доставить — см.
   [`../../../../Samples/InMemoryOutbox.ConsoleApp/`](../../../../Samples/InMemoryOutbox.ConsoleApp).

Часы: берите `TimeProvider` из DI и для публикации, и для лейзов, чтобы тесты двигали время, а не
спали.

## 10. Capabilities и несколько провайдеров

| Capability | Объявляйте, когда | Если не объявили |
|---|---|---|
| `PublishContext` | `InsertBulk` действительно входит в контекст вызывающего | non-null контекст обязан быть отвергнут |
| `TenantDetection` | вы регистрируете `IOutboxTenantDetector` | хост упадёт на старте, если флаг есть, а сервиса нет |
| `LockExtension` | `ExtendDelivery` действительно продлевает | ядро всё равно его вызывает; «ничего не продлено» останавливает батч |
| `Postponement` | вы соблюдаете задержку `Postpone`/`Retry` потребителя | ядро всё равно считает свой backoff для сбоев |

Несколько провайдеров сосуществуют, когда каждый зарегистрирован под своим `scopeKey`; повторная
регистрация того же ключа с другим `Name` — ошибка, как и два провайдера на один ключ. Все сервисы
провайдера стоит делать keyed тем же ключом — именно поэтому в §6 они регистрируются фабриками.

## См. также

- [`Readme-ru.md`](Readme-ru.md) — точка подключения, контракты и их XML-документация
- [`../InMemory/Readme-ru.md`](../InMemory/Readme-ru.md) — эталонная реализация с явной семантикой
  отмены и ретраев
- [`../Delivery/Readme-ru.md`](../Delivery/Readme-ru.md#отмена) — что конвейер делает с записанными
  вами статусами
