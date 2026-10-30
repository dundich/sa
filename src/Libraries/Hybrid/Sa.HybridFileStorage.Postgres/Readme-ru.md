# Sa.HybridFileStorage.Postgres

Провайдер файловых хранилищ на базе PostgreSQL для `Sa.HybridFileStorage`. Хранит файлы как `BYTEA` в партиционированной таблице с автоматическим управлением партициями, запланированной миграцией и фоновой очисткой.

---

## Содержание

- [Обзор](#обзор)
- [Формат File ID](#формат-file-id)
- [Установка](#установка)
- [Быстрый старт](#быстрый-старт)
  - [С DI](#с-di)
- [Примеры CRUD](#примеры-crud)
- [Партиционирование](#партиционирование)
- [Запланированное обслуживание](#запланированное-обслуживание)
- [Справочник настроек](#справочник-настроек)
- [Зависимости](#зависимости)

---

## Обзор

`PostgresFileStorage` реализует `IFileStorage` поверх PostgreSQL. Бинарные данные файлов хранятся в колонке `BYTEA` партиционированной таблицы. Ключевые особенности:

- **Keyed-регистрация** — каждая регистрация владеет своей таблицей, настройками, миграцией и очисткой; несколько независимых хранилищ coexists в одном контейнере
- **Автоматический data source** — chained `AddDataSource(...)` необязателен: без него хранилище переиспользует дефолтный `IPgDataSource` или уже зарегистрированный `NpgsqlDataSource`
- **Автоматическое партиционирование** — декларативное list + range партиционирование через `Sa.Partitional.PostgreSql`
- **Upsert-семантика** — `ON CONFLICT DO UPDATE` прозрачно обрабатывает повторные загрузки
- **Запланированная миграция** — фоновое задание заранее создаёт будущие партиции
- **Фоновая очистка** — удаляет старые партиции за пределами периода удержания
- **Обработка не-seekable потоков** — буферизует не-seekable потоки через `RecyclableMemoryStreamManager`
- **Таймстамп в File ID** — включает Unix-секунды для разрешения range-партиций

---

## Формат File ID

```
pg://{basket}/{tenantId}/{unixTimestamp}/{fileName}
```

**Примеры:**
- `pg://files/42/1751347200/report.pdf`
- `pg://docs/7/1751347200/invoice.csv`
- `pg://share/100/1751347200/data.bin`

> Таймстамп — это Unix-секунды полуночи UTC даты загрузки. Он определяет, к какой партиции относится строка.

---

## Установка

```powershell
dotnet add package Sa.HybridFileStorage.Postgres
```

---

## Быстрый старт

Регистрация **keyed**: каждый вызов принимает ключ регистрации, и каждый ключ даёт
независимое хранилище — со своими опциями (своим `TableName`, ретенцией и настройками
партиций), своим partitional-модулем и своим keyed `IFileStorage`. Тот же экземпляр
дополнительно регистрируется как *безымянный* `IFileStorage` — именно их hybrid-контейнер
собирает в failover-цепочку: несколько keyed-хранилищ перебираются в порядке регистрации,
и первый успех завершает операцию.

### С DI

```csharp
using Sa.HybridFileStorage.Postgres;

// Одно хранилище на ключ, каждое владеет своей таблицей.
builder.Services.AddSaPostgreSqlFileStorageChained("files", b => b
    .Options(ob => ob.Configure(o =>
    {
        o.TableName = "files";
        o.StorageType = "pg";
        o.Basket = "share";
        o.ExpireDays = 365 * 3;               // удалять партиции этого хранилища после 3 лет
    })))
    .AddDataSource(ds => ds
        .Options(ob => ob.Configure(o => o.ConnectionString =
            "Host=localhost;Database=mydb;Username=postgres;Password=password")));
```

**`TableName` обязателен и уникален.** У него нет значения по умолчанию: каждая регистрация
должна назвать свою таблицу, и две регистрации не могут объявить одну и ту же таблицу — они
обе запустили бы миграцию и очистку над одной физической таблицей, поэтому коллизия
отвергается на старте.

Если data source уже зарегистрирован чем-то другим, chained-перегрузка не нужна —
хранилище резолвит его автоматически (см. [Как подключиться к существующему NpgsqlDataSource](#как-подключиться-к-существующему-npgsqldatasource)):

```csharp
builder.Services.AddSaPostgreSqlFileStorage("binary", b => b
    .Options(ob => ob.Configure(o =>
    {
        o.TableName = "binary_data";
        o.SchemaName = "storage";
        o.PartByRange = PartByRange.Month;
    })));
```

Получить конкретное хранилище по ключу или отдать все в hybrid-pipeline:

```csharp
IFileStorage files  = sp.GetRequiredKeyedService<IFileStorage>("files");
IFileStorage binary = sp.GetRequiredKeyedService<IFileStorage>("binary");

// `sp.GetServices<IFileStorage>()` возвращает по одному экземпляру на регистрацию,
// в порядке регистрации — в этом порядке hybrid-failover пробует хранилища.
```

### Как подключиться к существующему NpgsqlDataSource

Chained `AddDataSource(...)` необязателен. Если для хранилища не настроен собственный
источник, он резолвится в таком порядке:

1. keyed `IPgDataSource` под ключом модуля этой регистрации (chained `AddDataSource`);
2. дефолтный (безымянный) `IPgDataSource`, если вызван `AddSaPostgreSqlDataSource(...)`;
3. уже зарегистрированный в контейнере `NpgsqlDataSource` — например, через
   `Npgsql.DependencyInjection` (`AddNpgsqlDataSource(...)`) или
   `services.AddSingleton<NpgsqlDataSource>(...)` — обёрнутый так, что **переиспользуется
   общий пул подключений** и никогда не диспозится хранилищем.

```csharp
// Пул у приложения уже есть; хранилище едет на нём — строки подключения здесь вообще нет.
builder.Services.AddNpgsqlDataSource("Host=db.internal;Database=mydb;Username=u;Password=p");
builder.Services.AddSaPostgreSqlFileStorage("files", b => b
    .Options(ob => ob.Configure(o => o.TableName = "files")));
```

Если нет ни `IPgDataSource`, ни `NpgsqlDataSource`, разрешение хранилища падает с
`InvalidOperationException`, называющим обе отсутствующие регистрации.

### Как определяется схема

`SchemaName` необязателен. При значении `null` схема определяется один раз на старте:

1. явно заданный `SchemaName`, если он есть;
2. первый элемент `search_path` из connection string
   (`...;Search Path=storage,public` → `storage`);
3. `public`.

Отдельного метода `WithSearchPath` у билдера data source нет: `search_path` задаётся
только через строку подключения. `GetSearchPath()` её парсит, обращения к БД не происходит.

### Где проходит валидация

Проверки идут через стандартный options-пайплайн — на **первом чтении** опций (их
материализует хранилище, DDL или резолвер схемы), либо на старте хоста через
`ValidateOnStart()`:

| Проблема | Ошибка |
|----------|--------|
| `TableName`, `SchemaName` — не «голый» SQL-идентификатор (`[A-Za-z_][A-Za-z0-9_]*`, ≤ 63 символов) | `OptionsValidationException` с именем свойства |
| `TableName` пуст/не задан | `OptionsValidationException` |
| `StorageType` длиннее 10 символов или содержит `:`, `/`, `\` (становится схемой file ID) | `OptionsValidationException` |
| `Basket` вне 3–63 символов, не начинается с буквы/`_`, или содержит разделитель пути | `OptionsValidationException` |
| `ExpireDays`, `MigrationScheduleForwardDays` `< 1` | `OptionsValidationException` |
| `MaxUploadSizeBytes` `< 0` | `OptionsValidationException` |
| `FileName` длиннее 512 символов или содержит NUL-байт | `ArgumentException` при загрузке |
| Две регистрации объявляют один и тот же `TableName` | `OptionsValidationException`, называющий оба ключа |
| Повторная регистрация с тем же **ключом** | `InvalidOperationException` на регистрации |
| `key` null/пуст/пробелы | `ArgumentException` на регистрации |

`TableName` используется и в DDL, и во всех запросах, поэтому некорректное значение
отвергается, а не переписывается молча: `"\"files\""` раньше тихо обрезалось до `files`.

---

## Примеры CRUD

### Загрузка из Stream

```csharp
using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, Postgres!"));
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "hello.txt", TenantId = 42 },
    stream, ct);

Console.WriteLine(result.FileId);
// pg://files/42/1751347200/hello.txt
// (timestamp = полуночь UTC сегодняшнего дня в Unix-секундах)
```

### Загрузка не-seekable потока

```csharp
// Не-seekable потоки автоматически буферизуются в RecyclableMemoryStreamManager
await using var nonSeekable = CreateNonSeekableStream();

var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "blob.dat", TenantId = 7 },
    nonSeekable, ct);

// Внутренне: скопирован → буферизован → upsert → буфер перезапущен
```

> Поток целиком буферизуется в памяти перед записью (Npgsql нужна длина заранее). Задайте
> `MaxUploadSizeBytes`, чтобы ограничить это выделение — слишком большая загрузка отклоняется
> `InvalidOperationException`, строка не пишется.

### Скачивание в память

```csharp
byte[]? downloaded = default;
await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    downloaded = await stream.ReadAllBytesAsync(token);
}, ct);
```

### Прямая обработка при скачивании

```csharp
await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    // Обрабатываем поток напрямую — без промежуточной буферизации
    using var reader = new BinaryReader(stream);
    while (reader.ReadByte() is byte b)
    {
        // ...
    }
}, ct);
```

### Получение метаданных

```csharp
var metadata = await storage.GetMetadataAsync(result.FileId, ct);
if (metadata != null)
{
    Console.WriteLine($"Корзина: {metadata.Basket}");       // files
    Console.WriteLine($"Тенант: {metadata.TenantId}");       // 42
    Console.WriteLine($"Имя: {metadata.FileName}");          // hello.txt
    Console.WriteLine($"Тип: {metadata.StorageType}");       // pg
}
```

`null` означает, что строки нет (удалена или файл никогда не загружался): значения читаются из
таблицы, а не восстанавливаются из File ID. Парсируемый ID сам по себе файлом не является.

То же и до первой записи: таблица создаётся первой загрузкой или расписанием миграции, поэтому
чтение до этого отвечает «не найдено» — `DownloadAsync`/`DeleteAsync` возвращают `false`,
`GetMetadataAsync` — `null`, а не `42P01`.

### Удаление

```csharp
bool deleted = await storage.DeleteAsync(result.FileId, ct);
// Парсит tenantId и timestamp из File ID для таргетированного DELETE
```

---

## Партиционирование

Файлы хранятся в партиционированной таблице с двойной стратегией:

1. **List-партиционирование** — по кортежу `(tenant_id, basket)`
2. **Range-партиционирование** — по `created_at` (дата)

### Авто-создание схемы

Провайдер использует `Sa.Partitional.PostgreSql` для управления партициями:

```sql
-- Автоматически созданная структура таблицы (сокращённо):
CREATE TABLE public.files (
    id         TEXT NOT NULL,
    name       VARCHAR(512) NOT NULL,
    size       BIGINT NOT NULL,
    file_ext   VARCHAR(64) NOT NULL,
    tenant_id  INT NOT NULL,
    basket     VARCHAR(63) NOT NULL,
    data       BYTEA NOT NULL,
    created_at BIGINT NOT NULL,       -- ключ range-партиционирования, Unix seconds (UTC-полночь дня загрузки)
    CONSTRAINT pk_files PRIMARY KEY (id, tenant_id, basket, created_at)
) PARTITION BY LIST (tenant_id);

-- Дерево вложенное, а не две независимые стратегии:
--   PARTITION BY LIST (tenant_id)  →  PARTITION BY LIST (basket)  →  PARTITION BY RANGE (created_at)
-- Поэтому каждая пара (tenant_id, basket) получает собственный набор партиций по датам.
```

> Каждый тенант (и бакет) получает собственный набор партиций по датам, поэтому дефолтные
> `PartByRange.Day` и `ExpireDays = 365 * 3` держат около 1095 листовых партиций на тенанта.
> При большом числе тенантов выбирайте `PartByRange.Month`/`Year` или меньший срок хранения —
> количество партиций и есть операционная цена этой схемы.

### Стратегии партиционирования

| Стратегия | Значение `PartByRange` | Сценарий использования |
|-----------|---------------------|----------------------|
| День | `PartByRange.Day` | Высоконагруженные системы, тонкая очистка |
| Месяц | `PartByRange.Month` | Средняя нагрузка, сбалансированная гранулярность |
| Год | `PartByRange.Year` | Низкая нагрузка, простое управление |

### Расписание миграции

Новые партиции создаются заранее (по умолчанию: за 2 дня) через фоновое задание:

```csharp
opts.MigrationScheduleForwardDays = 2;
```

### Расписание очистки

Старые партиции за пределами периода удержания удаляются через фоновое задание:

```csharp
opts.ExpireDays = 365 * 3;  // удалять партиции старше 3 лет
```

---

## Запланированное обслуживание

Для **каждой регистрации** автоматически регистрируются свои два фоновых задания:

| Задание | Назначение | Конфигурация |
|---------|-----------|-------------|
| **Миграция** | Заранее создавать будущие партиции этого хранилища | `ForwardDays`, `AsBackgroundJob` |
| **Очистка** | Удалять старые партиции после истечения срока этого хранилища | `DropPartsAfterRetention` (TimeSpan) |

Оба работают как фоновые hosted-сервисы и используют тот же пул подключений PostgreSQL, что
и хранилище.

---

## Справочник настроек

### PostgresFileStorageOptions

Опции плоские (без вложенных `PartOptions`/`CleanupOptions`/`StorageOptions`):

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `SchemaName` | Схема PostgreSQL (`null` = автоопределение из connection search_path, fallback `"public"`) | `null` |
| `TableName` | Имя таблицы для данных файлов — **обязательно**, уникально в рамках регистраций | — |
| `StorageType` | Префикс схемы в File ID | `"pg"` |
| `IsReadOnly` | Запрет операций записи/удаления | `false` |
| `Basket` | Имя контейнера (ключ list-партиции) | `"share"` |
| `PartByRange` | Гранулярность range-партиционирования | `PartByRange.Day` |
| `MigrationScheduleForwardDays` | Дней заранее для предсоздания партиций | `2` |
| `ExpireDays` | Период удержания перед удалением партиции (дни) | `365 * 3` |
| `MaxUploadSizeBytes` | Максимальный размер загрузки в байтах; `0` — без лимита | `0` |

### Методы регистрации

| Метод | Описание |
|-------|----------|
| `AddSaPostgreSqlFileStorage(string key, Action<IPostgresFileStorageBuilder>?)` | Регистрация keyed-провайдера; возвращает `IServiceCollection` |
| `AddSaPostgreSqlFileStorage(string key, PostgresFileStorageOptions)` | То же из готового экземпляра (копируется, не удерживается) |
| `AddSaPostgreSqlFileStorage(string key, IOptions<PostgresFileStorageOptions>)` | То же из готового options-экземпляра |
| `AddSaPostgreSqlFileStorageChained(string key, Action<IPostgresFileStorageBuilder>?)` | То же, возвращает `IPartConfiguration` для `.AddDataSource(...)` |

Ключи регистрации уникальны в рамках коллекции: повторный вызов с тем же ключом бросает
`InvalidOperationException` — он столкнулся бы на именованном options-экземпляре, на
keyed-модуле партиционирования и на keyed `IFileStorage`.

### DI-сервисы

| Сервис | Назначение |
|--------|------------|
| `IFileStorage` (keyed по ключу регистрации) | Сам провайдер (singleton на ключ) |
| `IFileStorage` (безымянный) | По одному alias на регистрацию — их hybrid-контейнер собирает в failover-цепочку |
| `PostgresFileStorageOptions` (именованы по ключу регистрации) | Живые опции хранилища; читаются через `IOptionsMonitor<...>.Get(key)` |
| `RecyclableMemoryStreamManager` | Буферизация не-seekable потоков (singleton, общий) |
| `IPartitionManager` (keyed) | Обслуживание партиций этого хранилища, из `Sa.Partitional.PostgreSql` |
| `TimeProvider` | По умолчанию `TimeProvider.System`; свой экземпляр влияет на `UploadedAt` |

---

## Зависимости

| Пакет | Назначение |
|-------|-----------|
| `Sa.Data.PostgreSql` | Npgsql клиент (`IPgDataSource`) |
| `Sa.Partitional.PostgreSql` | Управление партициями (`IPartitionManager`) |
| `Microsoft.IO.RecyclableMemoryStream` | Эффективная буферизация памяти для не-seekable потоков |

---

## Модель данных

Структура базовой таблицы:

| Колонка | Тип | Назначение |
|---------|-----|-----------|
| `id` | `TEXT` | Канонический File ID (часть первичного ключа) |
| `name` | `VARCHAR(512)` | Оригинальное имя файла |
| `size` | `BIGINT` | Размер файла в байтах |
| `file_ext` | `VARCHAR(64)` | Расширение файла (напр., "pdf", "png") |
| `tenant_id` | `INT` | Идентификатор тенанта (ключ list-партиции) |
| `basket` | `VARCHAR(63)` | Имя контейнера (ключ list-партиции) |
| `data` | `BYTEA` | Сырые бинарные данные файла |
| `created_at` | `BIGINT` | Дата загрузки (полуночь UTC в Unix-секундах, ключ range-партиции) |

`size` и `created_at` имеют тип `BIGINT`: провайдер пишет длину потока и Unix-метку
времени как 64-битные значения.

---

## Лицензия

MIT
