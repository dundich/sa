# Sa.HybridFileStorage

Гибридная абстракция файлового хранилища с автоматическим переключением между провайдерами. Объединяет несколько бэкендов (FileSystem, S3, PostgreSQL) под единым устойчивым API — если один провайдер становится недоступен, система переключается на другой.

---

## Поддерживаемые провайдеры

| Провайдер | Класс | Сценарий использования |
|-----------|-------|----------------------|
| **Файловая система** | `FileSystemStorage` | Локальная разработка, on-premise развёртывания |
| **S3-совместимое** | `S3FileStorage` | Облачное хранилище (AWS S3, MinIO и др.) |
| **PostgreSQL** | `PostgresFileStorage` | Файлы внутри БД, транзакционная согласованность |
| **In-Memory** | `InMemoryFileStorage` | Тестирование, эфемерные сценарии |

---

## Ключевые возможности

- ✅ **Единый API** — Один интерфейс для всех провайдеров хранения
- ✅ **Изоляция Basket/Tenant** — Многопользовательская поддержка со scoped корзинами
- ✅ **Режим «только чтение»** — Защита от случайных модификаций
- ✅ **Потоковая передача** — Эффективная работа с памятью при передаче файлов
- ✅ **Native AOT готово** — Полная совместимость с .NET 10 Native AOT
- ✅ **Пакетные операции** — Эффективная массовая обработка файлов с параллелизмом
- ✅ **Перехватчики (Interceptors)** — Хуки жизненного цикла загрузки/скачивания/удаления

---

## Формат File ID

Все файлы идентифицируются через унифицированный URI-подобный формат:

```
{storageType}://{basket}/{tenantId}/{fileName}
```

**Примеры:**
- `s3://share/42/document.pdf`
- `fs://root/100/report.xlsx`
- `pg://files/7/1773210911/some/data.bin`
- `mem://share/42/temp.txt`

---

## Быстрый старт

### Без DI

```csharp
using var memory = new InMemoryFileStorage(new InMemoryFileStorageOptions("share"));
var container = new HybridFileStorageContainer([memory]);
var storage = new HybridFileStorage(container, InterceptorContainer.Empty);

var stream = "Hello, HybridFileStorage!".ToStream();
var result = await storage.UploadAsync(
    "share",
    new UploadFileInput { FileName = "file.txt", TenantId = 42 },
    stream,
    ct);

await storage.DownloadAsync(result.FileId, async (fs, t) =>
{
    var content = await fs.ToStrAsync(t);
    Console.WriteLine(content);
});
```

### С DI

```csharp
builder.Services.AddSaHybridFileStorage(configure => configure
    .AddStorage(InMemoryFileStorage.New("share"))
    .AddLogging());

// Или регистрация отдельных провайдеров:
builder.Services.AddSaFileSystemFileStorage(new FileSystemStorageSettings
{
    BasePath = @"C:\data\files",
    Basket = "documents"
});

builder.Services.AddSaS3FileStorage(new S3FileStorageOptions
{
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    Bucket = "mybucket",
    Basket = "uploads"
});

// Использование:
var storage = serviceProvider.GetRequiredService<IHybridFileStorage>();
```

---

## Настройки

### FileSystemStorageSettings

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `BasePath` | Корневая директория для файлов | *(обязательно)* |
| `Basket` | Имя области хранения | `"share"` |
| `StorageType` | Префикс схемы в File ID | `"fs"` |
| `IsReadOnly` | Запрет записи | `false` |
| `BufferSize` | Размер буфера чтения/записи | `256 КБ` |

### S3FileStorageOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `Endpoint` | URL S3-эндпоинта | *(обязательно)* |
| `AccessKey` | Ключ доступа S3 | *(обязательно)* |
| `SecretKey` | Секретный ключ S3 | *(обязательно)* |
| `Bucket` | Имя бакета | *(обязательно)* |
| `Basket` | Имя области хранения | `"share"` |
| `Region` | Регион для SigV4 | `"eu-central-1"` |
| `IsReadOnly` | Запрет записи | `false` |

### PostgresFileStorageOptions

| Свойство | Описание |
|----------|----------|
| `SchemaName` | Схема PostgreSQL |
| `TableName` | Имя таблицы для данных файлов |
| `PartOptions.PgPartBy` | Стратегия партиционирования (day/month/year/list/range) |
| `CleanupOptions.ExpireDays` | Порог автоочистки |
| `StorageOptions.IsReadOnly` | Запрет записи |

### InMemoryFileStorageOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `Basket` | Имя области хранения | `"share"` |
| `IsReadOnly` | Запрет записи | `false` |

---

## Пакетные операции

`HybridFileStorageExtensions` предоставляет высокоуровневые методы для массовой обработки файлов с встроенным параллелизмом, обработкой ошибок и отчётами о прогрессе.

```csharp
// Копирование из локальной файловой системы
var result = await storage.CopyFromFileAsync(
    @"C:\temp\document.pdf",
    "archive",
    new UploadFileInput { FileName = "archived.pdf", TenantId = 42 });

// Копирование между корзинами/областями
var moved = await storage.CopyToBasketAsync(
    "s3://share/42/doc.pdf",
    "backup");

// Пакетное копирование с параллелизмом и прогрессом
var batchResult = await storage.CopyToScopeBatchAsync(
    fileIds: ["s3://share/1/a.txt", "s3://share/2/b.txt"],
    basket: "archive",
    options: new BatchOptions
    {
        MaxDegreeOfParallelism = 8,
        ContinueOnError = true,
        Progress = new Progress<BatchOperationProgress>()
    });

foreach (var ok in batchResult.Succeeded)
    Console.WriteLine($"Скопировано: {ok.FileId}");

foreach (var err in batchResult.Failed)
    Console.WriteLine($"Ошибка #{err.Index}: {err.FileId} — {err.Exception.Message}");
```

### BatchResult<T>

| Член | Тип | Описание |
|------|-----|----------|
| `Succeeded` | `IReadOnlyList<T>` | Успешные результаты |
| `Failed` | `IReadOnlyList<BatchError>` | Ошибки с File ID и исключением |
| `Total` | `int` | Всего обработанных элементов |
| `HasErrors` | `bool` | Были ли ошибки |
| `ThrowIfHasErrors()` | `void` | Выбрасывает `BatchOperationException<T>` при наличии ошибок |

### BatchOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `MaxDegreeOfParallelism` | Одновременные операции | `4` |
| `ContinueOnError` | Продолжать после ошибок | `true` |
| `OperationTimeout` | Таймаут на операцию | `0` (бесконечность) |
| `Progress` | Отчётчик прогресса | `null` |

---

## Перехватчики (Interceptors)

Хуки жизненного цикла для операций загрузки/скачивания/удаления.

```csharp
public interface IUploadInterceptor
{
    ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken ct);
    ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken ct);
    ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken ct);
}

public interface IDownloadInterceptor { /* аналогично Can/After/Error */ }
public interface IDeleteInterceptor { /* аналогично Can/After/Error */ }
```

Регистрация перехватчиков через fluent builder:

```csharp
services.AddSaHybridFileStorage(cfg => cfg.ConfigureInterceptors((sp, container) =>
{
    container.AddUploadInterceptor(myCustomInterceptor);
    container.AddDownloadInterceptor(loggingInterceptor);
}));
```

Встроенный `LoggingInterceptor` доступен через `.AddLogging()`.

---

## Режим «только чтение»

Установите `IsReadOnly = true` для любого провайдера хранилища, чтобы запретить запись. Попытки записи вызывают `HybridFileStorageWritableException`:

```csharp
builder.Services.AddSaFileSystemFileStorage(settings =>
{
    settings.BasePath = @"C:\readonly\data";
    settings.IsReadOnly = true;
});
```

---

## Доменные типы

### StorageResult

```csharp
public sealed record StorageResult(
    string FileId,
    string AbsoluteUrl,
    string StorageType,
    DateTimeOffset UploadedAt);
```

### UploadFileInput

```csharp
public sealed record UploadFileInput
{
    public int TenantId { get; init; }
    public string FileName { get; init; }
    public static UploadFileInput Empty { get; }
}
```

### FileMetadata

```csharp
public sealed class FileMetadata
{
    public required string Basket { get; init; }
    public required string FileName { get; init; }
    public int TenantId { get; init; }
    public required string StorageType { get; init; }
}
```

---

## Исключения

| Исключение | Когда выбрасывается |
|------------|-------------------|
| `HybridFileStorageNoAvailableException` | Не найдено хранилище для запрошенной корзины |
| `HybridFileStorageWritableException` | Попытка записи в хранилище «только чтение» |
| `HybridFileStorageAggregateException` | Несколько ошибок провайдеров агрегированы |
| `BatchOperationException<T>` | Пакет с ошибками и `ContinueOnError = false` |

---

## Структура проекта

```
src/Sa.HybridFileStorage/
├── IHybridFileStorage.cs           # Главный интерфейс
├── HybridFileStorage.cs             # Реализация с failover
├── HybridFileStorageContainer.cs    # Контейнер провайдеров
├── HybridStorageBuilder.cs          # Fluent builder
├── HybridFileStorageExtensions.cs   # Пакетные операции
├── Setup.cs                         # DI расширения
├── FileMetadata.cs                  # DTO метаданных
├── BatchResult.cs                   # Типы результатов пакетной обработки
└── Interceptors/                    # Хуки загрузки/скачивания/удаления

src/Sa.HybridFileStorage.FileSystem/  # Провайдер файловой системы
src/Sa.HybridFileStorage.S3/          # Провайдер S3
src/Sa.HybridFileStorage.Postgres/    # Провайдер PostgreSQL
```

---

## Лицензия

MIT
