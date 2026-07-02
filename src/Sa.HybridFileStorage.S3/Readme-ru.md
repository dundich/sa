# Sa.HybridFileStorage.S3

Провайдер S3-совместимого облачного хранилища для `Sa.HybridFileStorage`. Использует `Sa.Data.S3` клиент с автосозданием бакета, автоопределением MIME-типов и SigV4 подписью.

---

## Содержание

- [Обзор](#обзор)
- [Формат File ID](#формат-file-id)
- [Установка](#установка)
- [Быстрый старт](#быстрый-старт)
  - [Без DI](#без-di)
  - [С DI](#с-di)
- [Примеры CRUD](#примеры-crud)
- [Справочник настроек](#справочник-настроек)
- [Зависимости](#зависимости)

---

## Обзор

`S3FileStorage` реализует `IFileStorage` поверх S3-совместимых хранилищ (AWS S3, MinIO, DigitalOcean Spaces и др.). Файлы хранятся в бакете по структуре:

```
{Bucket}/{Basket}/{TenantId}/{FileName}
```

Ключевые особенности:
- **Автосоздание бакета** — если бакет не существует, создаётся при первой загрузке (потокобезопасно через `Interlocked.CompareExchange`)
- **Авто-MIME** — определяет `Content-Type` из расширения файла через `MimeTypeMap`
- **SigV4 подпись** — поддерживает AWS Signature Version 4
- **Потоковая передача** — потоки копируются напрямую без промежуточных буферов

---

## Формат File ID

```
s3://{basket}/{tenantId}/{fileName}
```

**Примеры:**
- `s3://uploads/42/document.pdf`
- `s3://media/7/video.mp4`
- `s3://share/100/data.bin`

---

## Установка

```powershell
dotnet add package Sa.HybridFileStorage.S3
```

---

## Быстрый старт

### Без DI

```csharp
using Sa.HybridFileStorage.S3;
using Sa.HybridFileStorage.Domain;

var options = new S3FileStorageOptions
{
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    Bucket = "mybucket",
    Basket = "uploads"
};

// Требуется предварительно зарегистрированный IS3BucketClient
var client = new S3BucketClient(new S3BucketClientSetupSettings
{
    Endpoint = options.Endpoint,
    AccessKey = options.AccessKey,
    SecretKey = options.SecretKey,
    Region = options.Region,
    Bucket = options.Bucket
});

using var storage = new S3FileStorage(client, options);

// Загрузка
using var stream = File.OpenRead(@"C:\temp\document.pdf");
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "document.pdf", TenantId = 42 },
    stream, ct);

Console.WriteLine(result.FileId);  // s3://uploads/42/document.pdf
Console.WriteLine(result.AbsoluteUrl);  // http://localhost:9000/mybucket/uploads/42/document.pdf
```

### С DI

```csharp
using Sa.HybridFileStorage.S3;

builder.Services.AddSaS3FileStorage(new S3FileStorageOptions
{
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    Bucket = "mybucket",
    Basket = "uploads"
});

// AddSaS3FileStorage автоматически регистрирует IS3BucketClient через AddSaS3BucketClient
```

---

## Примеры CRUD

### Загрузка из Stream

```csharp
using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, S3!"));
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "hello.txt", TenantId = 1 },
    stream, ct);

// Файл сохранён: mybucket/uploads/1/hello.txt
// Content-Type: text/plain (автоопределён из расширения)
```

### Загрузка больших файлов

```csharp
// Потоковая загрузка без загрузки в память
await using var fs = new FileStream(@"C:\large\data.zip", new FileStreamOptions
{
    Mode = FileMode.Open,
    Access = FileAccess.Read,
    Share = FileShare.Read,
    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
});

var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "data.zip", TenantId = 5 },
    fs, ct);
```

### Скачивание в память

```csharp
byte[]? downloaded = default;
await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    downloaded = await stream.ReadAllBytesAsync(token);
}, ct);
```

### Скачивание на диск

```csharp
await using var destination = new FileStream(@"C:\output\downloaded.pdf", FileMode.Create);
await storage.DownloadAsync(result.FileId, async (source, token) =>
    await source.CopyToAsync(destination, 1024 * 1024, token),  // 1 MB буфер
    ct);
```

### Получение метаданных

```csharp
var metadata = await storage.GetMetadataAsync(result.FileId, ct);
if (metadata != null)
{
    Console.WriteLine($"Корзина: {metadata.Basket}");       // uploads
    Console.WriteLine($"Тенант: {metadata.TenantId}");       // 42
    Console.WriteLine($"Имя: {metadata.FileName}");          // document.pdf
    Console.WriteLine($"Тип: {metadata.StorageType}");       // s3
}
```

### Удаление

```csharp
bool deleted = await storage.DeleteAsync(result.FileId, ct);
// Всегда возвращает true, если CanProcess(fileId) == true — S3 игнорирует отсутствие объектов
```

---

## Справочник настроек

### S3FileStorageOptions

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `Endpoint` | URL S3-совместимого эндпоинта | *(обязательно)* |
| `AccessKey` | Ключ доступа | *(обязательно)* |
| `SecretKey` | Секретный ключ | *(обязательно)* |
| `Bucket` | Имя целевого бакета | *(обязательно)* |
| `Basket` | Префикс области/контейнера внутри бакета | `"share"` |
| `Region` | Регион AWS для SigV4 подписи | `"eu-central-1"` |
| `StorageType` | Префикс схемы в File ID | `"s3"` |
| `IsReadOnly` | Запрет операций записи/удаления | `false` |

---

## Зависимости

| Пакет | Назначение |
|-------|-----------|
| `Sa.Data.S3` | S3 клиент (`IS3BucketClient`, `S3BucketClient`) |
| `Sa` | Общие утилиты (`MimeTypeMap`) |

Метод расширения `AddSaS3FileStorage` автоматически регистрирует `IS3BucketClient` через `AddSaS3BucketClient`.

---

## Лицензия

MIT
