# Sa.HybridFileStorage

Гибридная абстракция файлового хранилища с автоматическим переключением между провайдерами. Объединяет несколько бэкендов (FileSystem, S3, PostgreSQL) под единым устойчивым API — если один провайдер становится недоступен, система переключается на другой.

---

## Содержание

- [Концепция виртуальных папок (корзин)](#концепция-виртуальных-папок-корзин)
- [Поддерживаемые провайдеры](#поддерживаемые-провайдеры)
- [Ключевые возможности](#ключевые-возможности)
- [Формат File ID](#формат-file-id)
- [Быстрый старт](#быстрый-старт)
  - [Без DI](#без-di)
  - [С DI (Generic Host)](#с-di-generic-host)
- [Примеры CRUD](#примеры-crud)
  - [Загрузка](#загрузка)
  - [Скачивание](#скачивание)
  - [Удаление](#удаление)
  - [Получение метаданных](#получение-метаданных)
- [Копирование между корзинами](#копирование-между-корзинами)
- [Пакетные операции](#пакетные-операции)
- [Перехватчики (Interceptors)](#перехватчики-interceptors)
- [Режим «только чтение»](#режим-только-чтение)
- [Справочник настроек](#справочник-настроек)
- [Доменные типы](#доменные-типы)
- [Исключения](#исключения)
- [Структура проекта](#структура-проекта)

---

## Концепция виртуальных папок (корзин)

**Sa.HybridFileStorage** работает с концепцией **виртуальных папок** — они называются **корзины (baskets)**. Корзина — это логический строковый контейнер, лежащий поверх физических бэкендов хранения. С точки зрения приложения вы работаете с простыми именами папок: `"черновик"`, `"документы"`, `"архив"`. За каждым именем корзины скрывается провайдер хранения (или список провайдеров), организующих данные по-разному.

### Как корзины маппятся на хранилища

Каждая корзина поддерживается одним или несколькими провайдерами `IFileStorage`. Одно и то же имя корзины может обслуживаться разными физическими системами, а несколько провайдеров можно комбинировать для отказоустойчивости или многоуровневого хранения:

| Имя корзины | Физический бэкенд | Организация данных | Пример File ID |
|-------------|-------------------|--------------------|----------------|
| `черновик` | **Файловая система** (`fs://`) | Обычная древовидная структура на диске | `fs://черновик/42/заметки.txt` |
| `документы` | **PostgreSQL** (`pg://`) | Реляционная таблица с дата-партиционированием | `pg://документы/42/1751347200/договор.pdf` |
| `архив` | **S3 / MinIO** (`s3://`) | Облачный бакет с плоским пространством имён | `s3://архив/42/старый-отчёт.zip` |

За каждой корзиной может скрываться **одно хранилище или список хранилищ** — гибридный слой прозрачно управляет failover. Вам не нужно знать, какая физическая система хранит ваш файл — вы ссылаетесь на него только через File ID.

### Настройка маппинга корзин на бэкенды

Регистрируйте каждое соответствие корзина → провайдер явно. Один провайдер привязан ровно к одной корзине:

```csharp
builder.Services.AddSaHybridFileStorage(cfg => cfg
    // Корзина "черновик" → файловая система
    .ConfigureStorage((sp, c) => c.AddStorage(new FileSystemStorage(
        new FileSystemStorageSettings { BasePath = @"C:\data\черновик", Basket = "черновик" })))

    // Корзина "документы" → PostgreSQL с авто-партиционированием
    .ConfigureStorage((sp, c) => c.AddStorage(new PostgresFileStorage(dataSource, new PostgresFileStorageOptions
    {
        PartOptions = new() { Basket = "документы" },
        StorageOptions = new() { SchemaName = "files", TableName = "files" }
    })))

    // Корзина "архив" → S3 облачное хранилище
    .ConfigureStorage((sp, c) => c.AddStorage(new S3FileStorage(s3Client, new S3FileStorageOptions
    {
        Endpoint = "http://minio:9000",
        Bucket = "company-archive",
        Basket = "архив"
    }))));
```

После настройки все CRUD-операции работают с именами корзин, а не спецификой провайдеров:

```csharp
// Загрузка в виртуальную папку "черновик" — автоматически уходит на файловую систему
var result = await storage.UploadAsync("черновик", input, stream, ct);
// File ID: fs://черновик/42/мои-заметки.txt

// Скачивание из "документы" — маршрутизируется в PostgreSQL прозрачно
await storage.DownloadAsync(result.FileId, processStream, ct);

// Копирование из "черновик" в "архив" — беспрепятственно пересекает границу FS → S3
await storage.CopyToBasketAsync(result.FileId, "архив", ct);
```

---

## Поддерживаемые провайдеры

| Провайдер | Пакет | Класс | Сценарий использования |
|-----------|-------|-------|----------------------|
| **In-Memory** | `Sa.HybridFileStorage` | `InMemoryFileStorage` | Тестирование, эфемерные сценарии |
| **Файловая система** | `Sa.HybridFileStorage.FileSystem` | `FileSystemStorage` | Локальная разработка, on-premise развёртывания |
| **S3-совместимое** | `Sa.HybridFileStorage.S3` | `S3FileStorage` | Облачное хранилище (AWS S3, MinIO и др.) |
| **PostgreSQL** | `Sa.HybridFileStorage.Postgres` | `PostgresFileStorage` | Файлы внутри БД, транзакционная согласованность, партиционирование |

---

## Ключевые возможности

- ✅ **Единый API** — Один интерфейс `IHybridFileStorage` для всех провайдеров
- ✅ **Изоляция Basket/Tenant** — Многопользовательская поддержка со scoped контейнерами
- ✅ **Failover** — Автоматическое переключение провайдера при отказе бэкенда
- ✅ **Потоковая передача** — Эффективная работа с памятью через `Stream`
- ✅ **Native AOT готово** — Полная совместимость с .NET 10 Native AOT
- ✅ **Пакетные операции** — Массовая обработка файлов с настраиваемым параллелизмом
- ✅ **Перехватчики (Interceptors)** — Хуки жизненного цикла загрузки/скачивания/удаления
- ✅ **Режим «только чтение»** — Защита от случайных модификаций

---

## Формат File ID

Все файлы идентифицируются через унифицированный URI-подобный формат:

```
{storageType}://{basket}/{tenantId}/{path}
```

Каждый провайдер добавляет свою глубину пути:

| Провайдер | Пример File ID | Структура пути |
|-----------|---------------|----------------|
| **In-Memory** | `mem://share/42/document.pdf` | `{basket}/{tenantId}/{fileName}` |
| **Файловая система** | `fs://documents/100/report.xlsx` | `{basket}/{tenantId}/{fileName}` |
| **S3** | `s3://uploads/7/invoice.csv` | `{basket}/{tenantId}/{fileName}` |
| **PostgreSQL** | `pg://files/12/1751347200/photo.jpg` | `{basket}/{tenantId}/{unixTimestamp}/{fileName}` |

> **Примечание:** PostgreSQL включает Unix-таймстамп в пути, потому что партиционирует по дате. Другие провайды таймстамп не включают.

### Парсинг File ID

Используйте статический утилитный класс `FileIdParser`:

```csharp
if (FileIdParser.TryParse("pg://files/42/1751347200/report.pdf", out var basket, out var tenantId, out var timestamp, out var fileName))
{
    Console.WriteLine($"Basket={basket}, Tenant={tenantId}, TS={timestamp}, Name={fileName}");
    // Basket=files, Tenant=42, TS=1751347200, Name=report.pdf
}
```

---

## Быстрый старт

### Без DI

```csharp
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;

// 1. Создаём провайдер in-memory
using var memory = new InMemoryFileStorage(new InMemoryFileStorageOptions("share"));

// 2. Собираем гибридный контейнер
var container = new HybridFileStorageContainer([memory]);
var storage = new HybridFileStorage(container, InterceptorContainer.Empty);

// 3. Загружаем файл
var stream = "Hello, HybridFileStorage!".ToUtf8Stream();
var result = await storage.UploadAsync(
    basket: "share",
    input: new UploadFileInput { FileName = "hello.txt", TenantId = 42 },
    fileStream: stream,
    cancellationToken: ct);

Console.WriteLine(result.FileId);  // mem://share/42/hello.txt

// 4. Скачиваем и обрабатываем
bool wasFound = await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    using var reader = new StreamReader(stream, Encoding.UTF8);
    var content = await reader.ReadToEndAsync(token);
    Console.WriteLine(content);  // Hello, HybridFileStorage!
}, ct);

// 5. Удаляем
bool deleted = await storage.DeleteAsync(result.FileId, ct);
```

### С DI (Generic Host)

```csharp
using Microsoft.Extensions.Hosting;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.FileSystem;
using Sa.HybridFileStorage.S3;

var builder = Host.CreateApplicationBuilder(args);

// Регистрируем все провайдеры через fluent builder
builder.Services.AddSaHybridFileStorage(cfg => cfg
    // In-Memory провайдер
    .ConfigureStorage((sp, c) => c.AddStorage(new InMemoryFileStorage()))

    // Файловая система
    .ConfigureStorage((sp, c) => c.AddStorage(new FileSystemStorage(
        new FileSystemStorageSettings
        {
            BasePath = @"C:\data\files",
            Basket = "documents"
        })))

    // S3 провайдер
    .ConfigureStorage((sp, c) => c.AddStorage(
        new S3FileStorage(
            sp.GetRequiredService<IS3BucketClient>(),
            new S3FileStorageOptions
            {
                Endpoint = "http://localhost:9000",
                AccessKey = "ROOTUSER",
                SecretKey = "ChangeMe123",
                Bucket = "mybucket",
                Basket = "uploads"
            })))

    // Включаем встроенные логгирующие перехватчики
    .AddLogging());

var host = builder.Build();
var storage = host.Services.GetRequiredService<IHybridFileStorage>();

// Используйте везде — внедряется через DI в ваши сервисы
```

#### Минимальная регистрация DI

Для быстрых настроек каждый провайдер имеет собственный метод расширения:

```csharp
// Только In-Memory
builder.Services.AddSaInMemoryFileStorage();

// Только файловая система
builder.Services.AddSaFileSystemFileStorage(new FileSystemStorageSettings
{
    BasePath = @"C:\data\files",
    Basket = "documents"
});

// Только S3
builder.Services.AddSaS3FileStorage(new S3FileStorageOptions
{
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    Bucket = "mybucket",
    Basket = "uploads"
});

// Затем регистрируем гибридный слой
builder.Services.AddSaHybridFileStorage(cfg => cfg.AddLogging());
```

---

## Примеры CRUD

### Загрузка

Upload принимает имя корзины (контейнера), метаданные и `Stream`. Гибридный слой находит доступный провайдер, соответствующий корзине, и загружает файл.

```csharp
// Загрузка из Stream
using var stream = File.OpenRead(@"C:\temp\document.pdf");
var result = await storage.UploadAsync(
    basket: "documents",
    input: new UploadFileInput { FileName = "document.pdf", TenantId = 42 },
    fileStream: stream,
    cancellationToken: ct);

Console.WriteLine($"Загружено: {result.FileId}");
// Вывод: fs://documents/42/document.pdf
```

Копируем локальный файл напрямую:

```csharp
var result = await storage.CopyFromFileAsync(
    filePath: @"C:\temp\image.png",
    basket: "images",
    input: new UploadFileInput { FileName = "avatar.png", TenantId = 7 },
    ct: ct);
```

### Скачивание

Download делегирует поток файла колбэку `Func<Stream, CancellationToken, Task>`. Это избегает загрузки всего файла в память.

```csharp
// Обработка потока inline
bool found = await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    using var reader = new StreamReader(stream, Encoding.UTF8);
    string content = await reader.ReadToEndAsync(token);
    Console.WriteLine(content);
}, ct);

// Копирование в другой поток
using var destination = new FileStream(@"C:\output\copy.pdf", FileMode.Create);
await storage.DownloadAsync(result.FileId, async (source, token) =>
    await source.CopyToAsync(destination, 81920, token),
    ct);
```

### Удаление

```csharp
bool deleted = await storage.DeleteAsync(result.FileId, ct);
if (deleted)
    Console.WriteLine("Файл удалён.");
else
    Console.WriteLine("Файл не найден.");
```

### Получение метаданных

```csharp
var metadata = await storage.GetMetadataAsync(result.FileId, ct);
if (metadata != null)
{
    Console.WriteLine($"Корзина: {metadata.Basket}");
    Console.WriteLine($"Тенант:  {metadata.TenantId}");
    Console.WriteLine($"Имя:    {metadata.FileName}");
    Console.WriteLine($"Тип:    {metadata.StorageType}");
}
```

---

## Копирование между корзинами

Перемещайте или дублируйте файлы между корзинами (даже между разными провайдерами):

```csharp
// Копирование в пределах одной корзины
var copied = await storage.CopyToBasketAsync(
    fileId: "fs://documents/42/report.pdf",
    basket: "archive",
    ct: ct);

// Кастомизация метаданных при копировании
var renamed = await storage.CopyToBasketAsync(
    fileId: "fs://documents/42/report.pdf",
    basket: "backup",
    configure: meta => new UploadFileInput
    {
        TenantId = meta.TenantId,
        FileName = $"renamed-{meta.FileName}"  // меняем имя
    },
    ct: ct);
```

---

## Пакетные операции

Массовые параллельные операции с файлами, отчётами о прогрессе и обработкой ошибок:

```csharp
// Пакетное копирование с параллелизмом
var batchResult = await storage.CopyToScopeBatchAsync(
    fileIds:
    [
        "fs://documents/1/a.pdf",
        "fs://documents/2/b.pdf",
        "s3://uploads/3/c.pdf",
    ],
    basket: "archive",
    options: new BatchOptions
    {
        MaxDegreeOfParallelism = 8,
        ContinueOnError = true,
        OperationTimeout = TimeSpan.FromSeconds(30),
        Progress = new Progress<BatchOperationProgress>(p =>
        {
            Console.WriteLine($"{p.Completed}/{p.Total} — OK:{p.SuccessCount} Fail:{p.FailureCount}");
        })
    },
    ct: ct);

foreach (var ok in batchResult.Succeeded)
    Console.WriteLine($"Скопировано: {ok.FileId}");

foreach (var err in batchResult.Failed)
    Console.WriteLine($"Ошибка #{err.Index}: {err.FileId} — {err.Exception.Message}");

// Или выбросить исключение при любой ошибке
batchResult.ThrowIfHasErrors();  // выбрасывает BatchOperationException<StorageResult>
```

### BatchResult&lt;T&gt;

| Член | Тип | Описание |
|------|-----|----------|
| `Succeeded` | `IReadOnlyList<T>` | Успешные результаты |
| `Failed` | `IReadOnlyList<BatchError>` | Ошибки с File ID и исключением |
| `Total` | `int` | Всего обработано элементов |
| `HasErrors` | `bool` | Были ли ошибки |
| `ThrowIfHasErrors()` | `void` | Выбрасывает `BatchOperationException<T>` при наличии ошибок |

### BatchOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `MaxDegreeOfParallelism` | Одновременные операции | `4` |
| `ContinueOnError` | Продолжать после отдельных ошибок | `true` |
| `OperationTimeout` | Таймаут на операцию (`0` = бесконечно) | `0` |
| `Progress` | Отчётчик `IProgress<BatchOperationProgress>` | `null` |

---

## Перехватчики (Interceptors)

Хуки жизненного цикла для операций загрузки/скачивания/удаления. Реализуйте один из трёх интерфейсов:

```csharp
public interface IUploadInterceptor
{
    // Верните false для отклонения загрузки
    ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken ct);
    ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken ct);
    ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken ct);
}

public interface IDownloadInterceptor { /* CanDownloadAsync / AfterDownloadAsync / OnDownloadErrorAsync */ }
public interface IDeleteInterceptor  { /* CanDeleteAsync / AfterDeleteAsync / OnDeleteErrorAsync */ }
```

Пример — блокировка загрузки конкретных расширений:

```csharp
public class DeniedExtensionInterceptor : IUploadInterceptor
{
    private static readonly HashSet<string> DeniedExtensions = ["exe", "bat", "cmd"];

    public ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken ct)
    {
        var ext = Path.GetExtension(input.FileName)?.TrimStart('.').ToLowerInvariant();
        return ValueTask.FromResult(!DeniedExtensions.Contains(ext));
    }

    public ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken ct)
        => ValueTask.CompletedTask;
}
```

Регистрация перехватчиков через fluent builder:

```csharp
builder.Services.AddSaHybridFileStorage(cfg => cfg
    .ConfigureInterceptors((sp, container) =>
    {
        container.AddUploadInterceptor(new DeniedExtensionInterceptor());
        container.AddDownloadInterceptor(new LoggingDownloadInterceptor());
    }));
```

Встроенные логгирующие перехватчики доступны через `.AddLogging()`.

---

## Режим «только чтение»

Установите `IsReadOnly = true` для любого провайдера, чтобы запретить запись. Попытки записи вызывают `HybridFileStorageWritableException`:

```csharp
builder.Services.AddSaFileSystemFileStorage(new FileSystemStorageSettings
{
    BasePath = @"C:\readonly\data",
    IsReadOnly = true  // загрузки/удаления будут завершаться ошибкой
});
```

---

## Справочник настроек

### FileSystemStorageSettings

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `BasePath` | Корневая директория для файлов | *(обязательно)* |
| `Basket` | Имя контейнера (scopes) | `"share"` |
| `StorageType` | Префикс схемы в File ID | `"fs"` |
| `IsReadOnly` | Запрет записи | `false` |

### S3FileStorageOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `Endpoint` | URL S3-эндпоинта | *(обязательно)* |
| `AccessKey` | Ключ доступа S3 | *(обязательно)* |
| `SecretKey` | Секретный ключ S3 | *(обязательно)* |
| `Bucket` | Имя бакета | *(обязательно)* |
| `Basket` | Имя контейнера | `"share"` |
| `Region` | Регион для SigV4 подписи | `"eu-central-1"` |
| `StorageType` | Префикс схемы в File ID | `"s3"` |
| `IsReadOnly` | Запрет записи | `false` |

### PostgresFileStorageOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `StorageOptions.SchemaName` | Схема PostgreSQL | `"public"` |
| `StorageOptions.TableName` | Таблица для данных файлов | `"files"` |
| `StorageOptions.StorageType` | Префикс схемы в File ID | `"pg"` |
| `PartOptions.Basket` | Имя контейнера | `"share"` |
| `PartOptions.PgPartBy` | Гранулярность партиционирования | `PgPartBy.Day` |
| `PartOptions.MigrationScheduleForwardDays` | Дней заранее для предсоздания партиций | `2` |
| `CleanupOptions.ExpireDays` | Порог автоочистки (дней) | `365 * 3` |
| `StorageOptions.IsReadOnly` | Запрет записи | `false` |

### InMemoryFileStorageOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `Basket` | Имя контейнера | `"share"` |
| `MaxSizeBytes` | Лимит в байтах (`0` = без лимита) | `0` |
| `IsReadOnly` | Запрет записи | `false` |

---

## Доменные типы

### StorageResult

Результат операции загрузки. Содержит канонический File ID и публичный URL.

```csharp
public sealed record StorageResult(
    string FileId,          // напр. "fs://documents/42/report.pdf"
    string AbsoluteUrl,     // напр. "C:\data\files\documents\42\report.pdf"
    string StorageType,     // напр. "fs", "s3", "pg", "mem"
    DateTimeOffset UploadedAt);
```

### UploadFileInput

Входные метаданные для загрузки.

```csharp
public sealed record UploadFileInput
{
    public int TenantId { get; init; }              // по умолчанию 0
    public string FileName { get; init; } = "";     // обязательно при валидации
    public static UploadFileInput Empty { get; }    // предварительно созданный пустой экземпляр
}
```

### FileMetadata

Неизменяемые метаданные, полученные через `GetMetadataAsync`.

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
| `HybridFileStorageNoAvailableException` | Не найден провайдер для запрошенной корзины, либо все провайдеры завершились ошибкой |
| `HybridFileStorageWritableException` | Попытка записи в хранилище «только чтение» |
| `HybridFileStorageAggregateException` | Несколько ошибок провайдеров агрегированы при failover |
| `BatchOperationException<T>` | Пакетная операция имела ошибки и `ContinueOnError = false` |

---

## Структура проекта

```
src/Sa.HybridFileStorage/                          # Основная библиотека (NuGet: Sa.HybridFileStorage)
├── IHybridFileStorage.cs                          # Главный интерфейс
├── HybridFileStorage.cs                           # Реализация с failover + interceptors
├── HybridFileStorageContainer.cs                  # Контейнер провайдеров
├── HybridStorageBuilder.cs                        # Fluent DI builder
├── HybridFileStorageExtensions.cs                 # Пакетные операции (CopyFromFile, CopyToBasket, …)
├── Setup.cs                                       # DI расширения (AddSaHybridFileStorage, AddSaInMemoryFileStorage)
├── FileIdParser.cs                                # Утилита парсинга/форматирования File ID
├── FileMetadata.cs                                # DTO метаданных
├── InMemoryFileStorage.cs                         # In-memory провайдер
├── InMemoryFileStorageOptions.cs                  # Настройки in-memory
├── BatchResult.cs, BatchOptions.cs, …            # Типы пакетных операций
└── Interceptors/                                  # Хуки загрузки/скачивания/удаления
    ├── IUploadInterceptor.cs
    ├── IDownloadInterceptor.cs
    ├── IDeleteInterceptor.cs
    ├── UploadLoggingInterceptor.cs
    ├── DownloadLoggingInterceptor.cs
    └── DeleteLoggingInterceptor.cs

src/Sa.HybridFileStorage.FileSystem/               # Файловая система (NuGet: Sa.HybridFileStorage.FileSystem)
src/Sa.HybridFileStorage.S3/                       # S3 (NuGet: Sa.HybridFileStorage.S3)
src/Sa.HybridFileStorage.Postgres/                 # PostgreSQL (NuGet: Sa.HybridFileStorage.Postgres)
```

---

## Лицензия

MIT
