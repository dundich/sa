# Sa.Data.S3

Обёртка над `HttpClient` для S3-совместимых хранилищ (Minio, AWS S3, DigitalOcean Spaces и т.п.) со **своей реализацией AWS Signature Version 4** — без зависимостей от AWS SDK и Minio SDK.

---

## Фичи

- **Своя реализация AWS SigV4** — ни AWS SDK, ни Minio SDK в зависимостях: только `HttpClient` и `Microsoft.Extensions.*`.
- **Экономия памяти** — примерно в 150 раз ниже, чем у Minio SDK, и в 17 раз ниже, чем у AWS SDK, при сопоставимой со скоростью AWS.
- **[Именованные клиенты](#именованные-клиенты)** — `AddSaS3BucketClient("имя", ...)`: свой именованный `HttpClient` (пул соединений, resilience, время жизни handler'а), свои настройки и свой `IS3BucketClient` на каждое имя; резолв — `GetRequiredKeyedService<IS3BucketClient>("имя")`.
- **[Конфигурирование в appsettings](#конфигурирование-в-appsettings)** — секция `S3` через `FromConfiguration`, поверх — `Configure` / `PostConfigure` / `Validate`; неверная конфигурация роняет хост на старте, а не посреди первого запроса.
- **Файлы** — upload (включая multipart-загрузку через `S3Upload`), чтение потоком, перечисление по префиксу `List`, подписанные (presigned) ссылки `BuildFileUrl` / `GetFileUrl`, `IsFileExists`, `DeleteFile`.
- **Бакеты** — `CreateBucket`, `DeleteBucket`, `IsBucketExists`.
- **Транспорт под контролем** — таймаут запроса, время жизни пула соединений и handler'а, HTTP/2 — в `S3BucketClientSetupOptions`.
- **Интеграция с `Sa.HybridFileStorage.S3`** — провайдер `AddSaS3FileStorage` регистрирует клиента каждой папки той же именованной регистрацией.

---

## Быстрый старт

### Без DI

```csharp
using Sa.Data.S3;

var client = new S3BucketClient(
    new HttpClient { Timeout = TimeSpan.FromMinutes(3) },
    new S3BucketSettings
    {
        Bucket = "mybucket",
        Endpoint = "http://localhost:9000",
        AccessKey = "ROOTUSER",
        SecretKey = "ChangeMe123"
    });

CancellationToken ct = CancellationToken.None;

await client.UploadFile("docs/hello.txt", "text/plain", "hello"u8.ToArray(), ct);
using Stream stream = await client.GetFileStream("docs/hello.txt", ct);
```

Конвейер опций подключается только с DI-регистрацией; без неё транспорт задаётся на самом `HttpClient` (таймаут, пул соединений), а HTTP/2 — `S3BucketSettings.UseHttp2`.

### С DI

```csharp
services.AddSaS3BucketClient("my-client", o => o.Options(ob => ob.Configure(x =>
{
    x.Bucket = "mybucket";
    x.Endpoint = "http://localhost:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
})));
```

```csharp
IS3BucketClient client = serviceProvider.GetRequiredKeyedService<IS3BucketClient>("my-client");

await client.UploadFile("docs/hello.txt", "text/plain", "hello"u8.ToArray(), ct);

// Подписанная ссылка, живёт час; null, если файла нет.
string? url = await client.GetFileUrl("docs/hello.txt", TimeSpan.FromHours(1), ct);
```

Имя регистрации — ключ всего: именованного `HttpClient`, настроек, экземпляра опций и самого клиента. Несколько клиентов на хост и правила имён — в разделе [Именованные клиенты](#именованные-клиенты).

---

## Конфигурирование в appsettings

```json
{
  "S3": {
    "Endpoint": "http://minio:9000",
    "AccessKey": "ROOTUSER",
    "SecretKey": "ChangeMe123",
    "Bucket": "mybucket",
    "Region": "us-east-1",
    "TotalRequestTimeout": "00:03:00"
  }
}
```

```csharp
services.AddSaS3BucketClient("my-client", b =>
{
    b.FromConfiguration("S3");
    b.Options(ob => ob.Validate(
        v => !string.IsNullOrWhiteSpace(v.SecretKey),
        "SecretKey is required"));
});
```

`AddSaS3BucketClient` принимает единственный колбэк-билдер: `FromConfiguration` привязывает секцию, `Options(...)` отдаёт стандартный `OptionsBuilder<S3BucketClientSetupOptions>` — конфигурация идёт обычным конвейером опций, без отдельных перегрузок.

- Секция — обычный раздел конфигурации хоста: работают `appsettings.{Environment}.json`, переменные окружения (`S3__Bucket=mybucket`) и вся цепочка `IConfiguration`. `FromConfiguration` можно вызывать несколько раз — побеждает последний путь.
- Секция привязывается в фиксированном слоте **первой**, поэтому `Configure` из `Options(...)` имеет последнее слово — где бы эти вызовы ни стояли в колбэке. Действия `Options(...)` воспроизводятся после собственных `PostConfigure` и `Validate` регистрации, так что ваши проверки дополняют встроенные, а не заменяют их.
- **Валидация включена по умолчанию**: регистрация сама вызывает `ValidateOnStart()`, поэтому неверная секция даёт `OptionsValidationException` на старте хоста, а не голый `UriFormatException` посреди первой загрузки. Дополнительные проверки добавляются через `Validate(...)` в `Options(...)`; те же проверки доступны вне DI:

```csharp
options.Validate(); // бросает DataAnnotations.ValidationException
```

---

## Именованные клиенты

Каждая регистрация ключуется по имени, и имя обязано быть уникальным в коллекции: второй вызов `AddSaS3BucketClient` **под тем же именем** бросает `InvalidOperationException` — второй `Configure` налёг бы на тот же именованный экземпляр опций, а второй keyed-клиент под тем же ключом сделал бы резолв неоднозначным. Разные же имена дают полностью независимых клиентов: свой именованный `HttpClient` (пул соединений, resilience, время жизни handler'а), свои keyed `S3BucketSettings`, свой keyed `IS3BucketClient` и свой именованный экземпляр опций:

```csharp
services.AddSaS3BucketClient("orders", o => o.Options(ob => ob.Configure(x =>
{
    x.Endpoint = "http://minio:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
    x.Bucket = "orders-bucket";
})));

services.AddSaS3BucketClient("archive", o => o.Options(ob => ob.Configure(x =>
{
    x.Endpoint = "http://minio:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
    x.Bucket = "archive-bucket";
})));
```

Два клиента никогда не делят учётные данные, таймауты и пул соединений. Общая HTTP-обвязка под ними — внутренний `AddSaS3BucketClientCore(services, clientName, settingsFactory)`.

Резолв клиента — по имени регистрации; unkeyed-псевдонимов нет, поэтому `GetRequiredService<IS3BucketClient>()` без ключа возвращает `null`:

```csharp
IS3BucketClient ordersClient = serviceProvider.GetRequiredKeyedService<IS3BucketClient>("orders");
S3BucketSettings ordersSettings = serviceProvider.GetRequiredKeyedService<S3BucketSettings>("orders");
```

S3-провайдер `Sa.HybridFileStorage.S3` (`AddSaS3FileStorage` — по одному вызову на хранилище) ключует клиента каждой папки по имени регистрации точно так же, поэтому два хранилища — даже с одним эндпоинтом — остаются изолированными, а чтение, не нашедшее файл в первом бакете, продолжает пробор в следующем хранилище того же типа. Для `AddSaS3FileStorage` имя регистрации — внутренняя деталь: хранилище само резолвит свой клиент вместе со своими опциями, поэтому потребители никогда не зашивают ключ в код.

---

## Настройки

`S3BucketClientSetupOptions` наследует `S3BucketSettings`, поэтому в конструктор `S3BucketClient` передаётся тот же самый объект — между ними нет проекции, которая могла бы тихо потерять поле.

| Свойство | Описание | По умолчанию |
|----------|----------|--------------|
| `AccessKey` | access key S3 | *(обязательно)* |
| `SecretKey` | secret key S3 | *(обязательно)* |
| `Bucket` | имя бакета | *(обязательно)* |
| `Endpoint` | URL хранилища S3 (абсолютный http/https) | *(обязательно)* |
| `Region` | регион для SigV4 | `"us-east-1"` |
| `Service` | имя сервиса для SigV4 | `"s3"` |
| `UseHttp2` | принудительный HTTP/2 | `false` |
| `TotalRequestTimeout` | таймаут одного запроса | `180 сек` |
| `ConnectionPoolLifetime` | время жизни пула соединений | `15 мин` |
| `HandlerLifetime` | время жизни handler'а HttpClient | `∞` (бесконечность) |

`TotalRequestTimeout`, `ConnectionPoolLifetime` и `HandlerLifetime` относятся к именованному `HttpClient` DI-регистрации; при ручном создании клиента таймаут задаётся на самом `HttpClient`.

---

## Зачем

Это форк [teoadal/Storage](https://github.com/teoadal/Storage). Повод — потребление памяти: [AWS SDK for .NET](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/welcome.html) (4.x) и [Minio .NET](https://github.com/minio/minio-dotnet) (6.x) съедали слишком много памяти. Пакет сохранил идею лёгкого клиента на чистом `HttpClient`; результаты замеров скорости и памяти — в разделе [Фичи](#фичи).

---

## Лицензия

MIT
