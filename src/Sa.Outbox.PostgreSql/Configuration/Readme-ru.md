# Конфигурация

## Обзор

---

## Поток регистрации (DI)

### Цепочка входа

```
services.AddSaOutboxUsingPostgreSql(configure)   // корень пакета: Setup.cs (public)
  → AddOutboxSqlBuilder(configure)               // SqlBuilder/Setup.cs (internal)
      → AddPgOutboxSettings(configure)           // Configuration/Setup.cs (internal)
```

`AddPgOutboxSettings` создаёт объект конфигурации и применяет значения по умолчанию **до** делегата пользователя:

```csharp
var configuration = new PgOutboxConfiguration(services)
    .WithDefaultSerializer()   // ①
    .WithOutboxSettings()      // ②
    .WithDataSource();         // ③

configure?.Invoke(configuration);   // ④ делегат пользователя — всегда выполняется последним
```

### ① Сериализатор по умолчанию

`WithDefaultSerializer()` — `internal`-метод (не входит в `IPgOutboxConfiguration`). Он регистрирует общий `OutboxMessageSerializer.Instance` как singleton `IOutboxMessageSerializer` через `TryAddSingleton` — то есть любой `IOutboxMessageSerializer`, зарегистрированный раньше в вашем коде, имеет приоритет, а последующий вызов `WithMessageSerializer(...)` удаляет его и заменяет.

### ② Настройки outbox

`WithOutboxSettings(configure)`:

1. Если делегат передан, он регистрируется как singleton типа `Action<IServiceProvider, PgOutboxSettings>`.
2. `RegisterOutboxSettings()` регистрирует singleton `PgOutboxSettings` через **фабрику** (объект создаётся лениво при первом обращении, один на контейнер):
   - `new PgOutboxSettings()` — все четыре поднастройки стартуют как `new()`.
   - **Авто-наследование схемы**: фабрика резолвит `IPgDataSource` и читает `GetSearchPath()` (ключ Npgsql `Search Path` из connection string, либо `"public"`, если ключ отсутствует/непарсируется). Если значение не пусто, оно применяется как `settings.TableSettings.WithSchema(...)`. Это выполняется **до** делегатов пользователя, поэтому `WithSchema("x")` пользователя всегда перебивает search path соединения.
   - Затем **все** зарегистрированные делегаты `Action<IServiceProvider, PgOutboxSettings>` вызываются на этом едином инстансе в порядке регистрации. Последняя запись побеждает по каждому свойству.
3. `RegisterComponentSettings()` регистрирует ещё четыре singleton'а как **проекции** `PgOutboxSettings`:

   | Регистрируемый тип | Проецирует |
   |---|---|
   | `PgOutboxTableSettings` | `PgOutboxSettings.TableSettings` |
   | `PgOutboxMigrationSettings` | `PgOutboxSettings.MigrationSettings` |
   | `PgOutboxCleanupSettings` | `PgOutboxSettings.CleanupSettings` |
   | `PgOutboxConsumeSettings` | `PgOutboxSettings.ConsumeSettings` |

   Все через `TryAddSingleton`, поэтому любой внедрённый `PgOutboxMigrationSettings` — это тот самый объект, что и `PgOutboxSettings.MigrationSettings`.

### ③ Источник данных

`WithDataSource(configure)` делегирует в `AddSaPostgreSqlDataSource(configure)` из `Sa.Data.PostgreSql`:

- `IPgDataSourceSettingsBuilder` предоставляет `WithConnectionString(string)` и `WithConnectionString(Func<IServiceProvider, string>)`; любой из них регистрирует `PgDataSourceSettings` как singleton (`TryAddSingleton`).
- `IPgDataSource` регистрируется как singleton; его фабрика читает `PgDataSourceSettings`, если он зарегистрирован, иначе использует connection string зарегистрированного в DI `NpgsqlDataSource`, а если и того нет — бросает `InvalidOperationException("Empty connection string")`.
- `PgDataSourceSettings.GetSearchPath()` — источник авто-наследования схемы из шага ②.

### ④ Делегат пользователя

Ваш `configure`-делегат выполняется **последним** (после всех значений по умолчанию). Его три публичных метода:

- `WithMessageSerializer(...)` — удаляет все существующие регистрации `IOutboxMessageSerializer` (`RemoveAll`) и добавляет ровно одну новую (`TryAddSingleton`), поэтому всегда перебивает дефолтный JSON-сериализатор.
- `WithOutboxSettings(userAction)` — дописывает ваш делегат в зарегистрированный список `Action<IServiceProvider, PgOutboxSettings>`; он выполнится после ранее зарегистрированных делегатов.
- `WithDataSource(action)` — регистрирует `PgDataSourceSettings` через `TryAddSingleton`, поэтому побеждает только первая регистрация; фабрика `IPgDataSource` (зарегистрированная в шаге ③) подхватит её лениво при резолвинге.

### Где значения реально применяются

| Настройки | Потребитель | Эффект |
|---|---|---|
| `PgOutboxTableSettings` | `SqlOutboxBuilder` (все SQL-шаблоны) и `Partitional/Setup.cs` | Имена схемы / таблиц / колонок подставляются во все SQL-выражения; создание таблиц использует `PartByList` (tenant + part / consumer group), `TimestampAs` и `WithFillFactor` из этих настроек |
| `PgOutboxMigrationSettings` | `AddPartMigrationSchedule` → migration job из `Sa.Partitional.PostgreSql` | Job `StartImmediate()` + `EveryTime(ExecutionInterval)`; за один прогон гарантирует существование дневных партиций на сегодня .. сегодня + `ForwardDays − 1`; `AsBackgroundJob = false` → job `Disabled()` (остаётся резолвимым через `IMigrationService` для ручного запуска) |
| `PgOutboxCleanupSettings` | `AddPartCleanupSchedule` → cleanup job из `Sa.Partitional.PostgreSql` | Job выполняется `EveryTime(ExecutionInterval)`; за один прогон дропает партиции старше `now − DropPartsAfterRetention` по всем таблицам outbox; `AsBackgroundJob = false` → job `Disabled()` (остаётся резолвимым через `IPartCleanupService`) |
| `PgOutboxConsumeSettings` | `OutboxTaskLoader` | Нижняя граница сохранённого offset по группе консьюмеров — см. секцию `PgOutboxConsumeSettings` |

---

## IPgOutboxConfiguration

Публичная fluent-поверхность, получаемая как аргумент `configure` у `AddSaOutboxUsingPostgreSql(...)`.

| Метод | Назначение |
|---|---|
| `WithMessageSerializer(Func<IServiceProvider, IOutboxMessageSerializer>)` | Заменить сериализатор фабрикой, резолвимой из DI |
| `WithMessageSerializer<TService>(TService instance)` | Заменить сериализатор заранее созданным инстансом |
| `WithMessageSerializer<TService>()` | Зарегистрировать тип сериализатора с parameterless-конструктором |
| `WithOutboxSettings(Action<IServiceProvider, PgOutboxSettings>?)` | Зарегистрировать делегат, настраивающий `PgOutboxSettings` (таблицы / миграция / очистка / consume) |
| `WithDataSource(Action<IPgDataSourceSettingsBuilder>?)` | Настроить connection string PostgreSQL |

Все методы возвращают тот же `IPgOutboxConfiguration` для цепочки.

### `WithMessageSerializer` — три перегрузки

Каждая перегрузка сначала удаляет все существующие регистрации `IOutboxMessageSerializer` (`RemoveAll`), затем добавляет ровно одну — поэтому любая перегрузка всегда заменяет дефолтный JSON-сериализатор.

| Перегрузка | Регистрация в DI | Когда использовать |
|---|---|---|
| `Func<IServiceProvider, IOutboxMessageSerializer>` | `TryAddSingleton` с фабрикой | Сериализатор зависит от DI — например, читает опции из `IConfiguration` или получает source-generated `JsonSerializerContext` из контейнера |
| `TService instance` (`TService : class, IOutboxMessageSerializer`) | `TryAddSingleton` с инстансом | У вас уже есть готовый сериализатор (кастомные опции, зависимости в конструкторе); используется напрямую, без резолвинга из DI |
| `TService` (`TService : class, IOutboxMessageSerializer`) | `TryAddSingleton<IOutboxMessageSerializer, TService>` | Простой stateless-сериализатор с parameterless-конструктором. XML-документация называет это «transient», но код регистрирует **singleton** — один инстанс на жизнь контейнера |

### `WithOutboxSettings`

Принимает опциональный `Action<IServiceProvider, PgOutboxSettings>`. Если делегат не null, он сохраняется как singleton и затем применяется внутри фабрики `PgOutboxSettings` (см. Поток регистрации ②). Параметр `IServiceProvider` доступен, если при настройке нужно почитать другие зарегистрированные сервисы (например, `IConfiguration`).

### `WithDataSource`

Принимает опциональный `Action<IPgDataSourceSettingsBuilder>`. Единственная опция, доступная через билдер, — connection string (прямая строка или фабрика из DI). Пулинг и прочие опции Npgsql настраиваются внутри самого connection string (например, `Minimum Pool Size`, `Maximum Pool Size`, `Search Path`, `Timeout`); `Search Path` дополнительно управляет авто-наследованием схемы outbox-таблиц.

---

## PgOutboxSettings

Корневой объект настроек (public, sealed). Все четыре свойства **get-only** и инициализируются `new()`; объект поднастроек нельзя заменить — его только мутируют через свойства или fluent-методы.

```
PgOutboxSettings
├── TableSettings    (PgOutboxTableSettings)    — схема, шесть таблиц, имена колонок, fill factors
├── MigrationSettings (PgOutboxMigrationSettings) — расписание создания партиций
├── CleanupSettings   (PgOutboxCleanupSettings)  — расписание дропа старых партиций
└── ConsumeSettings   (PgOutboxConsumeSettings)  — минимальные offsets по группам консьюмеров
```

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `TableSettings` | `PgOutboxTableSettings` | `new()` | Схема и все шесть outbox-таблиц (имена, колонки, fill factors) |
| `MigrationSettings` | `PgOutboxMigrationSettings` | `new()` | Как часто и насколько вперёд работает миграция партиций |
| `CleanupSettings` | `PgOutboxCleanupSettings` | `new()` | Как часто дропаются старые партиции и окно retention |
| `ConsumeSettings` | `PgOutboxConsumeSettings` | `new()` | Минимальные offsets по группам консьюмеров |

> Примечание: XML-комментарий класса также упоминает «serialization, caching» — таких поднастроек в этом классе нет; формулировка устарела.

---

## PgOutboxTableSettings

### Топ-уровневые свойства

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `DatabaseSchemaName` | `string` | `"public"` | Схема, в которой живут все шесть outbox-таблиц. Авто-наследуется из `Search Path` соединения до пользовательской настройки; задайте явно, чтобы переопределить |
| `TaskQueue` | `TaskQueueTable` | `new()` | Активная очередь задач (read-write, `SKIP LOCKED`) — имя таблицы = базовое имя |
| `Message` | `MessageTable` | `new()` | Исходные сообщения (read-only, цель bulk BINARY COPY) |
| `Delivery` | `DeliveryTable` | `new()` | История доставок (read-only) |
| `Error` | `ErrorTable` | `new()` | Перманентные ошибки доставки |
| `Type` | `TypeTable` | `new()` | Реестр типов сообщений (type id ↔ имя); **нет свойства `FillFactor`** |
| `Offset` | `OffsetTable` | `new()` | Offsets по группам консьюмеров (advisory-лок); **нет свойства `FillFactor`** |

Публичный static-вложенный класс `Defaults` хранит две константы имён: `DatabaseSchemaName = "public"` и `DatabaseTableName = "outbox"` (базовое имя таблицы, от которого выводятся все шесть имён).

### Таблицы

| Свойство настроек | Класс | Имя таблицы по умолчанию | Суффикс | `FillFactor` по умолчанию | Роль |
|---|---|---|---|---|---|
| `TaskQueue` | `TaskQueueTable` | `outbox` | — | `65` | Активная очередь задач, read-write (`SKIP LOCKED`); 35% свободного места оставляет запас под in-place-обновления |
| `Message` | `MessageTable` | `outbox__msg$` | `__msg$` | `100` | Исходные сообщения, append-only (BINARY COPY), не обновляются никогда |
| `Delivery` | `DeliveryTable` | `outbox__log$` | `__log$` | `100` | История доставок, read-only |
| `Error` | `ErrorTable` | `outbox__error$` | `__error$` | `100` | Перманентные ошибки, read-only |
| `Type` | `TypeTable` | `outbox__type$` | `__type$` | — (нет свойства) | Реестр типов; создаётся вспомогательным SQL `SqlCreateTypeTable` (обычный `CREATE TABLE IF NOT EXISTS`, fill factor по умолчанию PostgreSQL) |
| `Offset` | `OffsetTable` | `outbox__offset$` | `__offset$` | — (нет свойства) | Offsets по группе консьюмеров + тенанту; создаётся вспомогательным SQL `SqlCreateOffsetTable` |

Все сеттеры `FillFactor` валидируют значение: `ArgumentOutOfRangeException` вне диапазона `1..100` (ограничение PostgreSQL).

### Колонки

Каждая таблица содержит get-only объект `Fields` с одним string-свойством на колонку; каждое свойство по умолчанию равно соответствующей константе `OutboxFieldDefaults`. Метод `Fields.All()` возвращает точные определения колонок PostgreSQL, используемые при создании таблицы.

#### Таблица сообщений (`__msg$`) — 8 колонок

| Свойство | Имя по умолчанию | Определение колонки | Описание |
|---|---|---|---|
| `MsgId` | `msg_id` | `UUID NOT NULL` | Id сообщения — UUID v7, генерируется приложением; sort-ключ партиционирования и offset потребления |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Id тенанта; первый ключ партиционирования |
| `MsgPart` | `msg_part` | `TEXT NOT NULL` | Часть (part) сообщения; второй ключ партиционирования |
| `MsgPayloadId` | `msg_payload_id` | `TEXT NOT NULL DEFAULT ''` | Опциональный payload id, передаваемый публикатором |
| `MsgPayloadType` | `msg_payload_type` | `BIGINT NOT NULL DEFAULT 0` | Хэш типа payload (murmurHash3), совпадает с реестром `__type$` |
| `MsgPayload` | `msg_payload` | `BYTEA NOT NULL` | Сериализованный payload сообщения |
| `MsgPayloadSize` | `msg_payload_size` | `INT NOT NULL DEFAULT 0` | Размер payload в байтах |
| `MsgCreatedAt` | `msg_created_at` | `BIGINT NOT NULL DEFAULT 0` | Время создания (ticks); timestamp-колонка партиционирования |

#### Таблица очереди задач (`outbox`) — 17 колонок

| Свойство | Имя по умолчанию | Определение колонки | Описание |
|---|---|---|---|
| `TaskId` | `task_id` | `BIGSERIAL NOT NULL` | Surrogate id задачи |
| `ConsumerGroup` | `consumer_group` | `TEXT NOT NULL` | Группа консьюмеров; второй ключ партиционирования |
| `TaskLockExpiresOn` | `task_lock_expires_on` | `BIGINT NOT NULL DEFAULT 0` | TTL блокировки (ticks) — конкуренты на `SKIP LOCKED` пропускают строки, у которых лок ещё действует |
| `TaskTransactId` | `task_transact_id` | `TEXT NOT NULL DEFAULT ''` | Id транзакции / корреляции воркера, обрабатывающего задачу |
| `MsgId` | `msg_id` | `UUID NOT NULL` | Ссылка на исходное сообщение (v7 UUID) |
| `MsgPart` | `msg_part` | `TEXT NOT NULL` | Часть сообщения (денормализована) |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Id тенанта; первый ключ партиционирования |
| `MsgPayloadId` | `msg_payload_id` | `TEXT NOT NULL DEFAULT ''` | Payload id (денормализован) |
| `MsgPayloadType` | `msg_payload_type` | `BIGINT NOT NULL DEFAULT 0` | Хэш типа (денормализован) |
| `MsgCreatedAt` | `msg_created_at` | `BIGINT NOT NULL DEFAULT 0` | Время создания сообщения (денормализовано) |
| `DeliveryId` | `delivery_id` | `BIGINT NOT NULL DEFAULT 0` | Id записи доставки в `__log$` |
| `DeliveryAttempt` | `delivery_attempt` | `INT NOT NULL DEFAULT 0` | Счётчик попыток доставки |
| `DeliveryStatusCode` | `delivery_status_code` | `INT NOT NULL DEFAULT 0` | Код статуса доставки; `0` = `DeliveryStatusCode.Pending` |
| `DeliveryStatusMessage` | `delivery_status_message` | `TEXT NOT NULL DEFAULT ''` | Текстовый статус / сообщение об ошибке |
| `DeliveryCreatedAt` | `delivery_created_at` | `BIGINT NOT NULL DEFAULT 0` | Время начала доставки (ticks) |
| `ErrorId` | `error_id` | `BIGINT NOT NULL DEFAULT 0` | Ссылка на запись `__error$` для перманентных сбоев |
| `TaskCreatedAt` | `task_created_at` | `BIGINT NOT NULL DEFAULT 0` | Время создания задачи; timestamp-колонка партиционирования |

#### Таблица доставки (`__log$`) — 12 колонок

| Свойство | Имя по умолчанию | Определение колонки | Описание |
|---|---|---|---|
| `DeliveryId` | `delivery_id` | `BIGSERIAL NOT NULL` | Id записи доставки |
| `DeliveryStatusCode` | `delivery_status_code` | `INT NOT NULL DEFAULT 0` | Итоговый статус доставки; `0` = `Pending` |
| `DeliveryStatusMessage` | `delivery_status_message` | `TEXT NOT NULL DEFAULT ''` | Сообщение статуса / об ошибке |
| `MsgPayloadId` | `msg_payload_id` | `TEXT NOT NULL DEFAULT ''` | Payload id |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Тенант; первый ключ партиционирования |
| `ConsumerGroup` | `consumer_group` | `TEXT NOT NULL` | Группа консьюмеров; второй ключ партиционирования |
| `TaskId` | `task_id` | `BIGINT NOT NULL DEFAULT 0` | Id исходной задачи |
| `TaskCreatedAt` | `task_created_at` | `BIGINT NOT NULL DEFAULT 0` | Время создания задачи |
| `TaskTransactId` | `task_transact_id` | `TEXT NOT NULL DEFAULT ''` | Id транзакции воркера |
| `TaskLockExpiresOn` | `task_lock_expires_on` | `BIGINT NOT NULL DEFAULT 0` | TTL блокировки на момент доставки |
| `ErrorId` | `error_id` | `BIGINT NOT NULL DEFAULT 0` | Ссылка на `__error$`, если доставка завершилась перманентной ошибкой |
| `DeliveryCreatedAt` | `delivery_created_at` | `BIGINT NOT NULL DEFAULT 0` | Время создания доставки; timestamp-колонка партиционирования |

#### Таблица ошибок (`__error$`) — 4 колонки

| Свойство | Имя по умолчанию | Определение колонки | Описание |
|---|---|---|---|
| `ErrorId` | `error_id` | `BIGINT NOT NULL` | Id записи об ошибке |
| `ErrorType` | `error_type` | `TEXT NOT NULL` | Имя типа ошибки |
| `ErrorMessage` | `error_message` | `TEXT NOT NULL DEFAULT ''` | Сообщение об ошибке |
| `ErrorCreatedAt` | `error_created_at` | `BIGINT NOT NULL DEFAULT 0` | Время создания; timestamp-колонка партиционирования |

#### Таблица типов (`__type$`) — 2 колонки

| Свойство | Имя по умолчанию | Определение колонки | Описание |
|---|---|---|---|
| `TypeId` | `type_id` | `BIGINT NOT NULL` | Хэш типа (murmurHash3) — первичный ключ |
| `TypeName` | `type_name` | `TEXT NOT NULL` | Полное имя типа |

#### Таблица offsets (`__offset$`) — 4 колонки

| Свойство | Имя по умолчанию | Определение колонки | Описание |
|---|---|---|---|
| `ConsumerGroup` | `consumer_group` | `TEXT` (nullable) | Id группы консьюмеров — часть первичного ключа |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Тенант — часть первичного ключа |
| `GroupOffset` | `group_offset` | `UUID NOT NULL DEFAULT '{Guid.Empty}'` | Последнее потреблённое `msg_id` (UUID v7) — offset является v7 UUID и потому упорядочен по времени |
| `GroupUpdatedAt` | `group_updated_at` | `TIMESTAMP WITH TIME ZONE DEFAULT NOW()` | Время последнего обновления offset |

### Fluent-методы (`PgOutboxTableSettingsExtensions`)

| Метод | Эффект |
|---|---|
| `GetQualifiedMsgTableName()` / `GetQualifiedDeliveryTableName()` / `GetQualifiedTypeTableName()` / `GetQualifiedOffsetTableName()` / `GetQualifiedErrorTableName()` / `GetQualifiedTaskTableName()` | Возвращают `"{DatabaseSchemaName}.\"{table}\""` для соответствующей таблицы — ровно тот формат, который используется во всех генерируемых SQL |
| `UseBaseTableName(string baseTableName)` | Переименовывает **все шесть** таблиц от одного базового имени: очередь задач = `base`, сообщения = `base__msg$`, доставка = `base__log$`, типы = `base__type$`, offsets = `base__offset$`, ошибки = `base__error$`. Бросает `ArgumentException` на null/whitespace |
| `UseBaseTableName(string schemaName, string baseTableName)` | То же, плюс устанавливает `DatabaseSchemaName = schemaName`. Бросает `ArgumentException` на null/whitespace схемы |
| `WithSchema(string schemaName)` | Устанавливает только `DatabaseSchemaName`. Бросает `ArgumentNullException` на null/whitespace |
| `WithMsgTableName(string tableName)` | Переименовывает только таблицу сообщений |
| `WithDeliveryTableName(string tableName)` | Переименовывает только таблицу доставки |
| `WithTypeTableName(string tableName)` | Переименовывает только таблицу типов |
| `WithOffsetTableName(string tableName)` | Переименовывает только таблицу offsets |
| `WithErrorTableName(string tableName)` | Переименовывает только таблицу ошибок |

Все перегрузки `With*TableName` бросают `ArgumentNullException` на null/whitespace. Обратите внимание: fluent-метода для переименования **только** очереди задач нет — используйте `UseBaseTableName(...)` (устанавливает имя очереди = базовое имя) либо задайте свойство `TaskQueue.TableName` напрямую.

### Пример

```csharp
settings.TableSettings
    .UseBaseTableName("outbox")        // outbox, outbox__msg$, outbox__log$, outbox__type$, outbox__offset$, outbox__error$
    .WithSchema("my_outbox");

// fill factor по таблицам
settings.TableSettings.TaskQueue.FillFactor = 65;   // read-write — оставляем свободное место под обновления
settings.TableSettings.Message.FillFactor = 100;    // append-only

// переименование отдельных колонок
settings.TableSettings.Message.Fields =
{
    MsgId = "id",
    MsgPayload = "payload_bytes"
};
```

---

## PgOutboxMigrationSettings

Управляет job-ом создания партиций (дневные партиции создаются для timestamp-колонок, отсортированных по времени).

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `AsBackgroundJob` | `bool` | `true` | `true` — job миграции расписан: стартует сразу при запуске host'а, затем повторяется каждые `ExecutionInterval`. `false` — job зарегистрирован, но `Disabled()`; `IMigrationService` по-прежнему резолвится, так что миграцию можно запустить вручную (например, `WaitMigration(...)`) |
| `ForwardDays` | `int` | `2` | Насколько вперёд двигается миграция: за один прогон гарантируется существование партиций на сегодня .. сегодня + `ForwardDays − 1` (т.е. `ForwardDays = 2` → сегодня и завтра). Это и «подтягивает» партиционирование вперёд по времени без ручного DDL |
| `ExecutionInterval` | `TimeSpan` | `6 ч` + случайные 1–58 мин | Интервал повтора job-а миграции. Значение по умолчанию — не фиксированные 6 часов: `6 ч + Random.Shared.Next(1, 59) минут` (рандом генерируется при создании объекта настроек). Jitter рассинхронизирует несколько реплик/инстансов, чтобы они не пытались мигрировать в один и тот же момент |

### Fluent-методы (`PgOutboxMigrationSettingsExtensions`)

| Метод | Эффект |
|---|---|
| `RunAsJob()` | `AsBackgroundJob = true` |
| `WithExecutionInterval(TimeSpan interval)` | Устанавливает `ExecutionInterval`; бросает `ArgumentException`, если `interval <= TimeSpan.Zero` |
| `UseProductionSettings()` | `AsBackgroundJob = true`, `ForwardDays = 7`, `ExecutionInterval = 6 ч + jitter 1–58 мин` |
| `UseDevelopmentSettings()` | `AsBackgroundJob = true`, `ForwardDays = 1`, `ExecutionInterval = 1 ч` |
| `UseTestSettings()` | `AsBackgroundJob = false`, `ForwardDays = 0` (без движения вперёд), `ExecutionInterval = 0` |

### Пример

```csharp
settings.MigrationSettings.UseProductionSettings();
// либо явно:
settings.MigrationSettings
    .RunAsJob()
    .WithExecutionInterval(TimeSpan.FromHours(6));
settings.MigrationSettings.ForwardDays = 3;
```

---

## PgOutboxCleanupSettings

Управляет job-ом дропа партиций: дневные партиции старше окна retention дропаются из всех таблиц outbox.

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `AsBackgroundJob` | `bool` | `true` | `true` — cleanup job работает по расписанию. `false` — job `Disabled()`; `IPartCleanupService` по-прежнему резолвится для ручного запуска |
| `DropPartsAfterRetention` | `TimeSpan` | `30 дней` | Окно retention: за один прогон дропаются партиции, чей timestamp-партиционирования старше `now − DropPartsAfterRetention`. Например, с дефолтом вчерашние партиции дропаются, когда им исполнится более 30 дней |
| `ExecutionInterval` | `TimeSpan` | `4 ч` | Интервал повтора cleanup job (свойство по умолчанию — ровно 4 часа, без jitter; jitter добавляет только `UseProductionSettings()`) |

### Fluent-методы (`PgOutboxCleanupSettingsExtensions`)

| Метод | Эффект |
|---|---|
| `RunAsJob()` | `AsBackgroundJob = true` |
| `RunImmediately()` | `AsBackgroundJob = false` |
| `SetAsJob(bool asJob)` | Явно устанавливает `AsBackgroundJob` |
| `WithRetentionPeriod(TimeSpan retentionPeriod)` | Устанавливает `DropPartsAfterRetention`; бросает `ArgumentException`, если `<= TimeSpan.Zero` |
| `WithRetentionDays(int days)` | `DropPartsAfterRetention = TimeSpan.FromDays(days)`; бросает `ArgumentException`, если `days <= 0` |
| `KeepForever()` | `DropPartsAfterRetention = TimeSpan.MaxValue` — партиции никогда не дропаются |
| `UseAggressiveCleanup()` | `AsBackgroundJob = true`, retention `7 дней`, interval `1 ч` |
| `UseConservativeCleanup()` | `AsBackgroundJob = true`, retention `90 дней`, interval `1 день` |
| `UseProductionSettings()` | `AsBackgroundJob = true`, retention `30 дней`, interval `4 ч + jitter 1–58 мин` |
| `UseTestSettings()` | `AsBackgroundJob = false`, retention `TimeSpan.MaxValue` (никогда не дропать), `ExecutionInterval = 0` |

Для cleanup нет `WithExecutionInterval` — для кастомного интервала задавайте свойство напрямую.

### Пример

```csharp
settings.CleanupSettings
    .RunAsJob()
    .WithRetentionDays(14);
settings.CleanupSettings.ExecutionInterval = TimeSpan.FromHours(2);
```

---

## PgOutboxConsumeSettings

**Минимальные offsets** по группам консьюмеров — нижняя граница сохранённого offset группы (public, sealed). Внутри это `Dictionary<string, Guid>` с ключом id группы консьюмеров.

| Член | Описание |
|---|---|
| `WithMinOffset(string consumerGroupId, Guid offset)` | Задаёт минимальный offset для группы (upsert — повторный вызов для той же группы заменяет значение). Ожидает реальный UUID v7 |
| `WithMinOffset(string consumerGroupId, DateTimeOffset offset)` | Преобразует дату-время в UUID v7 (`Guid.CreateVersion7(offset)`) и затем в его **минимальную форму** через `ToMinGuidV7()` — случайные биты v7 GUID обнуляются, результат — наименьший возможный v7 GUID для этого timestamp (чистая миллисекундная граница) |
| `GetMinOffset(string consumerGroupId)` | Возвращает заданный минимум, либо `Guid.Empty`, если для группы ничего не задано |

### Где применяется — `OutboxTaskLoader`

Для каждой пары «группа консьюмеров + тенант» loader:

1. берёт advisory-лок на группу,
2. читает сохранённый offset из таблицы `__offset$` (`SELECT group_offset WHERE consumer_group = ... AND tenant_id = ...`); если строки ещё нет, вставляет её, инициализируя сохранённым минимумом (или `Guid.Empty`),
3. вычисляет эффективный offset как `max(сохранённый offset, GetMinOffset(group))` — минимум это **пол**: сохранённый offset двигает консьюмера только вперёд, никогда ниже заданной границы,
4. загружает батч через `... WHERE msg_id > @offset ... ORDER BY msg_id LIMIT n` — поскольку `msg_id` это UUID v7, offset фактически означает «пропустить все сообщения, созданные до этой временной границы»,
5. сдвигает сохранённый offset на максимум `msg_id` загруженного батча.

Практический эффект: после утери offset, передеплоя или первого старта группы `WithMinOffset(group, DateTimeOffset.UtcNow)` заставит консьюмера начать «с сейчас», а не пережевывать всю историческую очередь.

### Пример

```csharp
settings.ConsumeSettings
    .WithMinOffset("cg_order_consumer", DateTimeOffset.UtcNow)  // пропустить всё, опубликованное до сейчас
    .WithMinOffset("cg_analytics", Guid.Parse("01913a2e-8f3c-7a1e-8f3c-0a2b3c4d5e6f"));
```

---

## OutboxFieldDefaults

Static-класс с именами колонок по умолчанию, которые используются каждым свойством `Fields`. Сгруппировано по таблицам, к которым они принадлежат:

| Группа | Константа | Значение по умолчанию |
|---|---|---|
| Общие (сообщение) | `MsgId` | `msg_id` |
| Общие (сообщение) | `TenantId` | `tenant_id` |
| Общие (сообщение) | `MsgPart` | `msg_part` |
| Общие (сообщение) | `MsgPayloadId` | `msg_payload_id` |
| Общие (сообщение) | `MsgPayloadType` | `msg_payload_type` |
| Общие (сообщение) | `MsgPayload` | `msg_payload` |
| Общие (сообщение) | `MsgPayloadSize` | `msg_payload_size` |
| Общие (сообщение) | `MsgCreatedAt` | `msg_created_at` |
| TaskQueue (`outbox`) | `TaskId` | `task_id` |
| TaskQueue (`outbox`) | `ConsumerGroup` | `consumer_group` |
| TaskQueue (`outbox`) | `TaskLockExpiresOn` | `task_lock_expires_on` |
| TaskQueue (`outbox`) | `TaskTransactId` | `task_transact_id` |
| TaskQueue (`outbox`) | `TaskCreatedAt` | `task_created_at` |
| Delivery (`__log$`) | `DeliveryId` | `delivery_id` |
| Delivery (`__log$`) | `DeliveryAttempt` | `delivery_attempt` |
| Delivery (`__log$`) | `DeliveryStatusCode` | `delivery_status_code` |
| Delivery (`__log$`) | `DeliveryStatusMessage` | `delivery_status_message` |
| Delivery (`__log$`) | `DeliveryCreatedAt` | `delivery_created_at` |
| Error (`__error$`) | `ErrorId` | `error_id` |
| Error (`__error$`) | `ErrorType` | `error_type` |
| Error (`__error$`) | `ErrorMessage` | `error_message` |
| Error (`__error$`) | `ErrorCreatedAt` | `error_created_at` |
| Type (`__type$`) | `TypeId` | `type_id` |
| Type (`__type$`) | `TypeName` | `type_name` |
| Offset (`__offset$`) | `GroupOffset` | `group_offset` |
| Offset (`__offset$`) | `GroupUpdatedAt` | `group_updated_at` |

Константы «Общие (сообщение)» разделяют несколько таблиц (одна и та же колонка `msg_id` есть в таблицах сообщений, очереди задач и доставки); `ConsumerGroup`, `TenantId`, `TaskId`, `TaskLockExpiresOn`, `TaskTransactId`, `TaskCreatedAt` и `ErrorId` разделяются между таблицами очереди задач / доставки / offsets, как показано выше.

---

## OutboxMessageSerializer

Сериализатор сообщений по умолчанию (internal, sealed). Реализует публичный интерфейс `IOutboxMessageSerializer` (`Serialize<T>(Stream, T)` / `Deserialize<T>(Stream)`) прямыми вызовами `JsonSerializer.Serialize` / `Deserialize` из `System.Text.Json` — то есть reflection-based, generic, не привязанный к типу. AOT-предупреждения (`IL2026` / `IL3050`) явно подавлены, поэтому общий `OutboxMessageSerializer.Instance` регистрируется через `WithDefaultSerializer()` и работает с любым типом сообщения.

Когда менять: (a) вы публикуете в **Native AOT** и хотите source-generated JSON (`[JsonSourceGenerationOptions]` + `JsonSerializerContext`) вместо рефлексии; (b) нужен не-JSON wire-формат (например, ProtoBuf, MessagePack); (c) нужны кастомные опции (naming policy, полиморфизм, конвертеры). Заменяйте любым из трёх перегрузок `WithMessageSerializer(...)` — см. секцию `IPgOutboxConfiguration`. Сам класс `internal`, поэтому пользовательский код может сослаться на общий инстанс только из кода с доступом `InternalsVisibleTo`; обычный потребитель просто не вызывает `WithMessageSerializer(...)` (получает дефолтный JSON) либо подаёт свой сериализатор (тип/инстанс/фабрика).

---

## Примеры использования

### 1. Кастомная схема, базовое имя таблиц и retention

```csharp
services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds
        .WithConnectionString("Host=localhost;Database=outbox_db;Username=postgres;Password=postgres"))
    .WithOutboxSettings((_, settings) =>
    {
        // схема + все шесть имён таблиц одним вызовом
        settings.TableSettings
            .UseBaseTableName("outbox")
            .WithSchema("my_outbox");

        // fill factor
        settings.TableSettings.TaskQueue.FillFactor = 65;
        settings.TableSettings.Message.FillFactor = 100;

        // обслуживание партиций
        settings.MigrationSettings
            .RunAsJob()
            .WithExecutionInterval(TimeSpan.FromHours(6));
        settings.MigrationSettings.ForwardDays = 3;

        settings.CleanupSettings
            .RunAsJob()
            .WithRetentionDays(30);
        settings.CleanupSettings.ExecutionInterval = TimeSpan.FromHours(4);
    })
);
```

### 2. Отключение background-миграции

```csharp
services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds.WithConnectionString("Host=localhost;Database=outbox_db;Username=postgres;Password=postgres"))
    .WithOutboxSettings((_, settings) =>
    {
        settings.MigrationSettings.AsBackgroundJob = false;  // job будет Disabled()
        // IMigrationService по-прежнему резолвится — миграцию можно запускать вручную:
        // var migration = host.Services.GetRequiredService<IMigrationService>();
        // await migration.WaitMigration(TimeSpan.FromSeconds(30), ct);
    })
);
```

### 3. Минимальный offset + кастомный сериализатор

```csharp
// ваш сериализатор: stateless, parameterless-конструктор
public sealed class OrderMessageSerializer : IOutboxMessageSerializer
{
    public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream);
    public void Serialize<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value);
}

services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds.WithConnectionString("Host=localhost;Database=outbox_db;Username=postgres;Password=postgres"))
    .WithOutboxSettings((_, settings) =>
    {
        // новая группа консьюмеров стартует «с сейчас» — без пережёвывания исторической очереди
        settings.ConsumeSettings
            .WithMinOffset("cg_order_consumer", DateTimeOffset.UtcNow)
            .WithMinOffset("cg_analytics", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    })
    // убирает дефолтный JSON-сериализатор и регистрирует ваш
    .WithMessageSerializer<OrderMessageSerializer>()
);
```

---

Полный quick start — см. Readme пакета: `src/Sa.Outbox.PostgreSql/Readme.md`.
