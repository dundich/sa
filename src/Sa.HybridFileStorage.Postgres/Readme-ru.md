# Sa.HybridFileStorage.Postgres

Провайдер файловых хранилищ на базе PostgreSQL для `Sa.HybridFileStorage`. Хранит файлы как `BYTEA` в партиционированной таблице с автоматическим управлением партициями, запланированной миграцией и фоновой очисткой.

---

## Содержание

- [Обзор](#обзор)
- [Формат File ID](#формат-file-id)
- [Установка](#установка)
- [Быстрый старт](#быстрый-старт)
  - [Без DI](#без-di)
  - [С DI](#с-di)
- [Примеры CRUD](#примеры-crud)
- [Партиционирование](#партиционирование)
- [Запланированное обслуживание](#запланированное-обслуживание)
- [Справочник настроек](#справочник-настроек)
- [Зависимости](#зависимости)

---

## Обзор

`PostgresFileStorage` реализует `IFileStorage` поверх PostgreSQL. Бинарные данные файлов хранятся в колонке `BYTEA` партиционированной таблицы. Ключевые особенности:

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

### Без DI

```csharp
using Sa.HybridFileStorage.Postgres;
using Sa.HybridFileStorage.Domain;

// Регистрация через fluent builder
builder.Services.AddSaPostgreSqlFileStorage(cfg => cfg
    .AddDataSource(ds => ds
        .WithConnectionString("Host=localhost;Database=mydb;Username=postgres;Password=password")
        .WithSearchPath("public"))
    .WithSchemaName("public")
    .WithTableName("files")
    .WithStorageType("pg")
    .ConfigureOptions((sp, options) =>
    {
        // Настройка партиционирования
        options.PartOptions.Basket = "files";
        options.PartOptions.PgPartBy = PgPartBy.Day;
        options.PartOptions.MigrationScheduleForwardDays = 2;

        // Настройка очистки
        options.CleanupOptions.ExpireDays = 365 * 3;  // 3 года
    }));
```

### С DI

```csharp
using Sa.HybridFileStorage.Postgres;

builder.Services.AddSaPostgreSqlFileStorage(cfg => cfg
    .AddDataSource(ds => ds
        .WithConnectionString("Host=db.example.com;Database=app;Username=app_user;Password=secret")
        .WithSearchPath("storage"))
    .WithTableName("binary_data")
    .WithSchemaName("storage")
    .ConfigureOptions((sp, opts) =>
    {
        opts.PartOptions.Basket = "attachments";
        opts.PartOptions.PgPartBy = PgPartBy.Month;
        opts.CleanupOptions.ExpireDays = 730;  // 2 года
    }));
```

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
-- Автоматически созданная структура таблицы:
CREATE TABLE public.files (
    id         TEXT NOT NULL,
    name       TEXT NOT NULL,
    size       INT NOT NULL,
    file_ext   TEXT NOT NULL,
    tenant_id  INT NOT NULL,
    basket     TEXT NOT NULL,
    data       BYTEA NOT NULL,
    created_at TIMESTAMPTZ NOT NULL  -- используется для range-партиционирования
) PARTITION BY RANGE (created_at);

-- Каждая пара (tenant_id, basket) получает собственную list-партию внутри каждого диапазона дат
```

### Стратегии партиционирования

| Стратегия | Значение `PgPartBy` | Сценарий использования |
|-----------|---------------------|----------------------|
| День | `PgPartBy.Day` | Высоконагруженные системы, тонкая очистка |
| Месяц | `PgPartBy.Month` | Средняя нагрузка, сбалансированная гранулярность |
| Год | `PgPartBy.Year` | Низкая нагрузка, простое управление |

### Расписание миграции

Новые партиции создаются заранее (по умолчанию: за 2 дня) через фоновое задание:

```csharp
.ConfigureOptions((sp, opts) =>
{
    opts.PartOptions.MigrationScheduleForwardDays = 2;
})
```

### Расписание очистки

Старые партиции за пределами периода удержания удаляются через фоновое задание:

```csharp
.ConfigureOptions((sp, opts) =>
{
    opts.CleanupOptions.ExpireDays = 365 * 3;  // удалять партиции старше 3 лет
})
```

---

## Запланированное обслуживание

Автоматически регистрируются два фоновых задания:

| Задание | Назначение | Конфигурация |
|---------|-----------|-------------|
| **Миграция** | Заранее создавать будущие партиции | `forwardDays`, `asBackgroundJob` |
| **Очистка** | Удалять старые партиции после истечения срока | `dropPartsAfterRetention` (TimeSpan) |

Оба работают как фоновые hosted-сервисы и используют общий пул подключений PostgreSQL.

---

## Справочник настроек

### PostgresFileStorageOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `StorageOptions.SchemaName` | Схема PostgreSQL | `"public"` |
| `StorageOptions.TableName` | Имя таблицы для данных файлов | `"files"` |
| `StorageOptions.StorageType` | Префикс схемы в File ID | `"pg"` |
| `StorageOptions.IsReadOnly` | Запрет операций записи/удаления | `false` |
| `PartOptions.Basket` | Имя контейнера (ключ list-партиции) | `"share"` |
| `PartOptions.PgPartBy` | Гранулярность range-партиционирования | `PgPartBy.Day` |
| `PartOptions.MigrationScheduleForwardDays` | Дней заранее для предсоздания партиций | `2` |
| `CleanupOptions.ExpireDays` | Период удержания перед удалением партиции (дни) | `365 * 3` |

### IPostgresFileStorageConfiguration (fluent builder)

| Метод | Описание |
|-------|----------|
| `AddDataSource(Action<IPgDataSourceSettingsBuilder>?)` | Настроить подключение PostgreSQL |
| `WithSchemaName(string)` | Переопределить имя схемы |
| `WithTableName(string)` | Переопределить имя таблицы |
| `WithStorageType(string)` | Переопределить идентификатор типа хранилища |
| `AsReadOnly()` | Пометить как read-only |
| `ConfigureOptions(Action<IServiceProvider, PostgresFileStorageOptions>)` | Поздняя кастомизация |

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
| `name` | `TEXT` | Оригинальное имя файла |
| `size` | `INT` | Размер файла в байтах |
| `file_ext` | `TEXT` | Расширение файла (напр., "pdf", "png") |
| `tenant_id` | `INT` | Идентификатор тенанта (ключ list-партиции) |
| `basket` | `TEXT` | Имя контейнера (ключ list-партиции) |
| `data` | `BYTEA` | Сырые бинарные данные файла |
| `created_at` | `TIMESTAMPTZ` | Дата загрузки (полуночь UTC, ключ range-партиции) |

---

## Лицензия

MIT
