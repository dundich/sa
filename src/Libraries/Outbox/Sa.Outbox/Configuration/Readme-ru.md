# Конфигурация

**Русский** | [English](Readme.md)

Регистрация консьюмеров, настройки потребления и публикации, сериализация, мультитенантность и runtime-управление в ядре `Sa.Outbox`. Часть документации [`Sa.Outbox`](../Readme-ru.md); хранилище и доставку предоставляет провайдер — см. [Доступные провайдеры](../Readme-ru.md#доступные-провайдеры).

## Содержание

- [Регистрация консьюмеров](#регистрация-консьюмеров)
- [Управление настройками во время выполнения](#управление-настройками-во-время-выполнения)
- [Настройки потребления](#настройки-потребления)
- [Политика ожидания пустой очереди](#политика-ожидания-пустой-очереди)
- [Глобальные переопределения DI](#глобальные-переопределения-di)
- [Настройки публикации](#настройки-публикации)
- [Сериализация](#сериализация)
- [Мультитенантность](#мультитенантность)
- [Метаданные сообщений](#метаданные-сообщений)

## Регистрация консьюмеров

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

Настройки группы можно брать и из `IConfiguration`, а не из кода: у `AddDelivery` /
`AddDeliveryScoped` есть необязательный `configurationKey`, а ядро по соглашению применяет секцию
`Outbox:DeliveryGroups:{group}` — префикс меняется (или отключается) через
`WithDeliveryConfigurationSection(...)`:

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveryConfigurationSection("Outbox:DeliveryGroups") // по умолчанию; null отключает соглашение
    .WithDeliveries(d => d.AddDeliveryScoped<OrderConsumer, OrderPlaced>("orders"))
);
```

Само соглашение реализовано в ядре, а биндинг секции — в пакете-провайдере: правила для PostgreSQL
(nullable-свойства, приоритеты, что намеренно исключено) — в
[`Configuration/Readme-ru.md`](../../Sa.Outbox.PostgreSql/Configuration/Readme-ru.md#pgoutboxdeliveryoptions).

## Управление настройками во время выполнения

`IOutboxConsumerManager` позволяет менять настройки без перезапуска:

```csharp
// Атомарный свап — новый снимок применяется атомарно
manager.Apply("orders", s => s with { MaxBatchSize = 64 });

// Пауза / Возобновление
manager.Pause("orders");
manager.Unpause("orders");

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

У каждого члена есть и перегрузка с `OutboxGroupKey(providerKey, groupId)`: при нескольких
провайдерах один и тот же id группы может существовать под разными ключами, поэтому строковые
перегрузки адресуют провайдера по умолчанию и бросают ошибку неоднозначности, если id не уникален.
Строковые ключи и их перегрузки сохранены для однопровайдерных приложений.

## Настройки потребления

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
| `RetryCountOnError` | 0 | Повторы job (не сообщения) при ошибке; `WithInfiniteRetries()` ставит `int.MaxValue`, отрицательные значения отклоняются |
| `Paused` | false | Флаг паузы |
| `IdleMaxInterval` | 0 (выключено) | Верхняя граница адаптивного backoff-а пустой очереди; при установке должен быть `>= Interval` |

## Политика ожидания пустой очереди

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

Пример собственной реализации `IEmptyQueueWaitStrategy` — в [`Delivery/Readme-ru.md`](../Delivery/Readme-ru.md) → *Настройка политики ожидания*.

---

## Глобальные переопределения DI

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveryBatcher<MyBatcher>()          // свой расчёт размера батча
    .WithIdleWaitStrategy<MyIdleStrategy>()    // своя стратегия ожидания пустой очереди
);
```

Оба необязательны: `IDeliveryBatcher` по умолчанию — `DeliveryBatcher` (проброс `MaxBatchSize`), `IEmptyQueueWaitStrategy` — `ExponentialIdleWaitStrategy`. Стратегия ожидания по умолчанию неактивна, пока группа не включит её через `WithIdleBackoff(maxInterval)`, поэтому «из коробки» опрос идёт с фиксированным `Interval`.

---

## Настройки публикации

`WithPublishSettings` настраивает `OutboxPublishSettings` (сейчас — `MaxBatchSize`, по умолчанию 256) для одного
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

## Сериализация

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

## Мультитенантность

Арендатор задаётся выбором **одного** из режимов ниже — они взаимоисключающие,
поэтому в одной регистрации используется лишь одна цепочка.

```csharp
// Режим 1: фиксированный список ID арендаторов
.WithTenants((_, ts) => ts.WithTenantIds(1, 2, 3))
```

```csharp
// Режим 2: автодетект арендаторов из сообщений в рантайме
.WithTenants((_, ts) => ts.WithAutoDetect())
```

Сам детектор — забота провайдера: пакет-провайдер поставляет свой (PostgreSQL читает колонку
арендатора из таблицы сообщений), а собственный подключается на уровне провайдера —
`AddOutboxProvider(..., p => p.WithTenantDetector<MyDetector>())`, см.
[`PlugServices/Readme-ru.md`](../PlugServices/Readme-ru.md). Параллелизм по арендаторам — это настройка
консьюмер-группы, а не источника арендаторов: `WithPerTenantMaxDegreeOfParallelism(3)` (или
`WithTenantMaxParallelism()` — один воркер на ядро) у `OutboxConsumerSettingsBuilder`.

> **Примечание:** `WithTenantIds`, `WithTenantSource` и `WithTenantProvider` все
> помечают источник арендаторов как авторитетный, отключая автодетект внутри
> (эквивалент добавления `.WithNoAutoDetect()`). Любой из них можно сочетать с
> параллелизмом по арендаторам на уровне консьюмера, но никогда — с цепочкой автодетекта выше. Как только
> любой из них вызван, ветка автодетекта через plug-сервис пропускается и
> переданный список/источник/провайдер используется без изменений.
>
> Два режима взаимоисключающие и проверяются на этапе конфигурации: вызов
> `WithAutoDetect()` после любого из них бросит <c>InvalidOperationException</c> вместо
> тихого игнорирования явного источника. Выберите один режим на регистрацию.

---

## Метаданные сообщений

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
