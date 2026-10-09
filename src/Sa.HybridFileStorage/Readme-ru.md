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

Каждый провайдер привязан ровно к одной корзине, которая задаётся в его собственных опциях.
Провайдеры, регистрирующие себя методом `Add...` (файловая система, in-memory), подхватываются
контейнером автоматически:

```csharp
// Корзина "черновик" → файловая система. Регистрирует собственный IFileStorage под
// именем "черновик"; корень и ввод-вывод живут на temp-folder с тем же именем.
builder.Services.AddSaFileSystemFileStorage("черновик", b => b
    .Options(ob => ob.Configure(x => x.Basket = "черновик"))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder =>
        folder.RootPath = @"C:\data\черновик"))));

// Корзина "архив" → S3 облачное хранилище: bucket-клиент — именованная регистрация
// под техническим ключом "archive", резолв по нему же.
builder.Services.AddSaS3BucketClient("archive", o => o.Options(ob => ob.Configure(x =>
{
    x.Endpoint = "http://minio:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
    x.Bucket = "company-archive";
})));

builder.Services.AddSaHybridFileStorage(cfg => cfg
    // Корзина "документы" → PostgreSQL с авто-партиционированием.
    // Зависимости (IPgDataSource, IPartitionManager, RecyclableMemoryStreamManager)
    // резолвятся из DI в момент регистрации.
    .ConfigureStorage((sp, c) => c.AddStorage(new PostgresFileStorage(
        sp.GetRequiredService<IPgDataSource>(),
        sp.GetRequiredService<IPartitionManager>(),
        sp.GetRequiredService<RecyclableMemoryStreamManager>(),
        new PostgresFileStorageOptions
        {
            Basket = "документы",
            TableName = "files"
        })))

    // Корзина "архив" → S3 облачное хранилище
    .ConfigureStorage((sp, c) => c.AddStorage(new S3FileStorage(
        sp.GetRequiredKeyedService<IS3BucketClient>("archive"),
        new S3FileStorageOptions
        {
            Endpoint = "http://minio:9000",
            Bucket = "company-archive",
            Basket = "архив"
        }))));
```

Несколько вызовов `ConfigureStorage` накапливаются: каждый вызов регистрирует дополнительные storage в контейнере, поэтому в одной цепочке `AddSaHybridFileStorage` можно повесить несколько корзин.

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

| Провайдер | Пакет | Регистрация | Сценарий использования |
|-----------|-------|-------------|----------------------|
| **In-Memory** | `Sa.HybridFileStorage` | `AddSaInMemoryFileStorage()` | Тестирование, эфемерные сценарии |
| **Файловая система** | `Sa.HybridFileStorage.FileSystem` | `AddSaFileSystemFileStorage(...)` | Локальная разработка, on-premise развёртывания |
| **S3-совместимое** | `Sa.HybridFileStorage.S3` | `AddSaS3FileStorage(...)` | Облачное хранилище (AWS S3, MinIO и др.) |
| **PostgreSQL** | `Sa.HybridFileStorage.Postgres` | `AddSaPostgreSqlFileStorage(...)` | Файлы внутри БД, транзакционная согласованность, партиционирование |

> Метод `Add...` каждого провайдера регистрирует `IFileStorage`, и гибридный контейнер
> автоматически подхватывает все зарегистрированные `IFileStorage`. Сами классы провайдеров
> внутренние — создавайте их через метод `Add...`, а не через `new`.

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
using Sa.Data.S3;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.FileSystem;
using Sa.HybridFileStorage.S3;

var builder = Host.CreateApplicationBuilder(args);

// Провайдеры, регистрирующие себя сами, — гибридный контейнер подхватывает любой
// зарегистрированный IFileStorage автоматически, вызов ConfigureStorage для них не нужен.
builder.Services.AddSaInMemoryFileStorage();
builder.Services.AddSaFileSystemFileStorage("files", b => b
    .Options(ob => ob.Configure(x => x.Basket = "documents"))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder =>
        folder.RootPath = @"C:\data\files"))));

// Bucket-клиент S3 — именованная регистрация: по одному имени на клиент, резолв по ключу.
builder.Services.AddSaS3BucketClient("uploads", o => o.Options(ob => ob.Configure(x =>
{
    x.Endpoint = "http://localhost:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
    x.Bucket = "mybucket";
})));

// Затем собираем контейнер
builder.Services.AddSaHybridFileStorage(cfg => cfg
    // S3 провайдер — здесь показан ручной вариант
    .ConfigureStorage((sp, c) => c.AddStorage(
        new S3FileStorage(
            sp.GetRequiredKeyedService<IS3BucketClient>("uploads"),
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
builder.Services.AddSaFileSystemFileStorage("files", b => b
    .Options(ob => ob.Configure(x => x.Basket = "documents"))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder =>
        folder.RootPath = @"C:\data\files"))));

// Только S3
builder.Services.AddSaS3FileStorage(o => o.Options(ob => ob.Configure(x =>
{
    x.Endpoint = "http://localhost:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
    x.Bucket = "mybucket";
    x.Basket = "uploads";
})));

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
builder.Services.AddSaFileSystemFileStorage("readonly", b => b
    .Options(ob => ob.Configure(x =>
    {
        x.IsReadOnly = true; // загрузки/удаления будут завершаться ошибкой
    }))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder =>
        folder.RootPath = @"C:\readonly\data"))));
```

---

## Справочник настроек

### FileSystemStorageOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `Basket` | Имя контейнера (scopes) | `"share"` |
| `StorageType` | Префикс схемы в File ID | `"fs"` |
| `IsReadOnly` | Запрет записи | `false` |

Корень хранилища и политика ввода-вывода настраиваются на именованном temp-folder через
обязательный канал `TempFolder(...)` (`RootPath`, `MaxAge`, `TouchDebounce`, ...). Регистрация
только именованная: `AddSaFileSystemFileStorage(name, b => b…TempFolder(…))`. См.
[`Sa.HybridFileStorage.FileSystem/Readme-ru.md`](../Sa.HybridFileStorage.FileSystem/Readme-ru.md)
про pre/post-инициализацию, привязку конфигурации и миграцию с `BasePath`.

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
| `TotalRequestTimeout` | Таймаут одного запроса | `180 сек` |
| `ConnectionPoolLifetime` | Время жизни пула соединений | `15 мин` |
| `HandlerLifetime` | Время жизни handler'а HttpClient | `∞` (бесконечность) |

Настраивается через стандартный конвейер options — см.
[`Sa.HybridFileStorage.S3/Readme-ru.md`](../Sa.HybridFileStorage.S3/Readme-ru.md)
про pre/post-инициализацию, привязку конфигурации и валидацию.

### PostgresFileStorageOptions

Опции плоские (без вложенных `PartOptions`/`CleanupOptions`/`StorageOptions`):

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `SchemaName` | Схема PostgreSQL (автоопределяется из search_path, если не задано) | `"public"` |
| `TableName` | Таблица для данных файлов | `"files"` |
| `StorageType` | Префикс схемы в File ID | `"pg"` |
| `Basket` | Имя контейнера | `"share"` |
| `PgPartBy` | Гранулярность партиционирования | `PgPartBy.Day` |
| `MigrationScheduleForwardDays` | Дней заранее для предсоздания партиций | `2` |
| `ExpireDays` | Порог автоочистки (дней) | `365 * 3` |
| `IsReadOnly` | Запрет записи | `false` |

### InMemoryFileStorageOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `Basket` | Имя контейнера | `"share"` |
| `MaxSizeBytes` | Лимит в байтах (`0` = без лимита) | `1 GB` |
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

## Лицензия

MIT
