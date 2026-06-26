# Sa.Data.S3

Обёртка над `HttpClient` для работы с S3-совместимыми хранилищами (Minio, AWS S3, DigitalOcean Spaces и др.). Полностью собственная реализация AWS Signature Version 4 — **без зависимостей от AWS SDK или Minio SDK**.

## Мотивация

Это форк https://github.com/teoadal/Storage. Мотивация — клиенты [AWS SDK for .NET](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/welcome.html) (4.x) и [Minio .NET](https://github.com/minio/minio-dotnet) (6.x) потребляли слишком много памяти. Результат: скорость почти как у AWS, а потребление памяти в ~150 раз меньше чем Minio SDK и в ~17 раз меньше AWS SDK.

## Создание клиента

### Без DI

```csharp
var client = new S3BucketClient(new HttpClient(), new S3BucketClientSetupSettings
{
    Bucket = "mybucket",
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123"
});
```

### С DI

```csharp
services.AddSaS3BucketClient(new S3BucketClientSetupSettings
{
    Bucket = "mybucket",
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    TotalRequestTimeout = TimeSpan.FromSeconds(180),
    ConnectionPoolLifetime = TimeSpan.FromMinutes(15),
    HandlerLifetime = Timeout.InfiniteTimeSpan // или TimeSpan.FromHours(2) для периодического обновления handler
});

// Использование:
var client = serviceProvider.GetRequiredService<IS3BucketClient>();
```

## Настройки

| Свойство | Описание | По умолчанию |
|---|---|---|
| `AccessKey` | Ключ доступа S3 | *(обязательно)* |
| `SecretKey` | Секретный ключ S3 | *(обязательно)* |
| `Bucket` | Имя бакета | *(обязательно)* |
| `Endpoint` | URL S3-хранилища | *(обязательно)* |
| `Region` | Регион для SigV4 | `"us-east-1"` |
| `Service` | Сервис для SigV4 | `"s3"` |
| `UseHttp2` | Принудительный HTTP/2 | `false` |
| `TotalRequestTimeout` | Таймаут каждого запроса | `180 сек` |
| `ConnectionPoolLifetime` | Время жизни пула соединений | `15 мин` |
| `HandlerLifetime` | Время жизни HttpClient handler | `∞` (бесконечность) |

## API

### IBucketOperations

```csharp
public interface IBucketOperations
{
    Task<bool> CreateBucket(CancellationToken ct);
    Task<bool> DeleteBucket(CancellationToken ct);
    Task<bool> DeleteBucket(bool forceDelete, CancellationToken ct); // force: удалить все объекты перед удалением bucket
    Task<bool> IsBucketExists(CancellationToken ct);
}
```

### IFileOperations

```csharp
public interface IFileOperations
{
    string BuildFileUrl(string fileName);
    string BuildFileUrl(string fileName, TimeSpan expiration);
    Task DeleteFile(string fileName, CancellationToken ct);
    Task<S3File> GetFile(string fileName, CancellationToken ct);
    Task<Stream> GetFileStream(string fileName, CancellationToken ct);
    Task<string?> GetFileUrl(string fileName, TimeSpan expiration, CancellationToken ct);
    Task<bool> IsFileExists(string fileName, CancellationToken ct);
    IAsyncEnumerable<string> List(string? prefix, CancellationToken ct); // с pagination
    Task<bool> UploadFile(string fileName, string contentType, byte[] data, CancellationToken ct);
    Task<S3Upload> UploadFile(string fileName, string contentType, CancellationToken ct); // ручной multipart
    Task<bool> UploadFile(string fileName, string contentType, Stream data, CancellationToken ct);
}
```

### Rучной Multipart Upload

Для файлов > 5MB автоматически выбирается multipart upload. Для ручного управления:

```csharp
using var uploader = await client.UploadFile("large-file.bin", "application/octet-stream", ct);

uploader.AddPart(chunkData, ct);
uploader.AddPart(chunkData, offset, length, ct); // перегрузка с offset
uploader.AddParts(fullDataStream, ct);
uploader.AddParts(fullByteArray, ct);

if (await uploader.Complete(ct))
{
    Console.WriteLine($"Uploaded {uploader.Written} bytes");
}
else
{
    await uploader.Abort(ct);
}
```

## Особенности реализации

- **AWS SigV4** — полная ручная реализация подписывания запросов (SHA256 + HMAC-SHA256 chain)
- **ArrayPool<T>.Shared** — пулинг буферов для минимизации GC pressure
- **ref struct ValueStringBuilder** — стек-based строковый билдер без аллокаций
- **stackalloc** — везде где возможно для избежания heap allocation
- **Буферизированный XML парсер** — эффективное чтение ответов S3 (ListObjects, Multipart IDs)
- **Pagination** — автоматическая обработка `IsTruncated` / `NextContinuationToken` в `List()`
- **CancellationToken** — поддерживается во всех async операциях
