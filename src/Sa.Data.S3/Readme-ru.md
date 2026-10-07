# Sa.Data.S3

Обёртка над `HttpClient` для работы с S3-совместимыми хранилищами (Minio, AWS S3, DigitalOcean Spaces и т.п.). Полностью своя реализация AWS Signature Version 4 — **без зависимостей от AWS SDK и Minio SDK**.

---

## Зачем

Это форк https://github.com/teoadal/Storage. Причина: [AWS SDK for .NET](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/welcome.html) (4.x) и [Minio .NET](https://github.com/minio/minio-dotnet) (6.x) съедали слишком много памяти. Результат: скорость сопоставима с AWS, а потребление памяти примерно в 150 раз ниже, чем у Minio SDK, и в 17 раз ниже, чем у AWS SDK.

---

## Создание клиента

### Без DI

```csharp
var client = new S3BucketClient(new HttpClient(), new S3BucketClientSetupOptions
{
    Bucket = "mybucket",
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123"
});
```

### С DI

`AddSaS3BucketClient(имя, ...)` принимает стандартный колбэк опций, поэтому конфигурация идёт через `Configure` / `PostConfigure` / `Validate` как у любого другого типа опций, а не через отдельный перегруженный метод. Имя однозначно идентифицирует клиента: это ключ именованного `HttpClient` (пул соединений, resilience, время жизни handler'а), настроек, экземпляра опций и самого клиента — и именно по нему происходит резолв:

```csharp
services.AddSaS3BucketClient("my-client", o => o.Options(ob => ob.Configure(x =>
{
    x.Bucket = "mybucket";
    x.Endpoint = "http://localhost:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
    x.TotalRequestTimeout = TimeSpan.FromSeconds(180);
    x.ConnectionPoolLifetime = TimeSpan.FromMinutes(15);
    x.HandlerLifetime = Timeout.InfiniteTimeSpan; // либо TimeSpan.FromHours(2) для периодического обновления handler'а
})));

// Использование — резолв по имени регистрации:
var client = serviceProvider.GetRequiredKeyedService<IS3BucketClient>("my-client");
```

### Из секции конфигурации

```csharp
// appsettings.json:
// { "S3": { "Endpoint": "http://localhost:9000", "AccessKey": "…", "SecretKey": "…", "Bucket": "mybucket" } }
services.AddSaS3BucketClient("my-client", b => b.FromConfiguration("S3"));
```

Секция привязывается в фиксированном слоте **первой**, поэтому `Configure` из `Options(...)` имеет последнее слово. Действия `Options(...)` воспроизводятся после собственных `PostConfigure` и `Validate` регистрации, так что ваши проверки дополняют встроенные, а не заменяют их.

### Валидация

Значения нормализуются (`PostConfigure`), а затем проверяются при чтении; `ValidateOnStart()` превращает неверную конфигурацию в `OptionsValidationException` на старте хоста, а не в голый `UriFormatException` посреди первой загрузки. Те же проверки доступны вне DI:

```csharp
options.Validate(); // бросает DataAnnotations.ValidationException
```

### Одна регистрация на имя, много именованных клиентов на хост

Каждая регистрация ключуется по имени, и имя обязано быть уникальным в коллекции: второй вызов
`AddSaS3BucketClient` **под тем же именем** бросает `InvalidOperationException` — второй `Configure`
налёг бы на тот же именованный экземпляр опций, а второй keyed-клиент под тем же ключом сделал бы
резолв неоднозначным. Разные же имена дают полностью независимых клиентов: свой именованный
`HttpClient` (пул соединений, resilience, время жизни handler'а), свои keyed `S3BucketSettings`,
свой keyed `IS3BucketClient` и свой именованный экземпляр опций:

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

Два клиента никогда не делят учётные данные, таймауты и пул соединений. Общая HTTP-обвязка под
ними — внутренний `AddSaS3BucketClientCore(services, clientName, settingsFactory)`. S3-провайдер
`Sa.HybridFileStorage.S3` (`AddSaS3FileStorage` — по одному вызову на хранилище) ключует клиента
каждой папки по имени регистрации точно так же, поэтому два хранилища — даже с одним эндпоинтом —
остаются изолированными, а чтение, не нашедшее файл в первом бакете, продолжает пробор в
следующем хранилище того же типа.

Резолв клиента — по имени регистрации; unkeyed-псевдонимов нет, поэтому
`GetRequiredService<IS3BucketClient>()` без ключа возвращает `null`:

```csharp
IS3BucketClient ordersClient = serviceProvider.GetRequiredKeyedService<IS3BucketClient>("orders");
S3BucketSettings ordersSettings = serviceProvider.GetRequiredKeyedService<S3BucketSettings>("orders");
```

Для `AddSaS3FileStorage` имя регистрации — внутренняя деталь: хранилище само резолвит свой клиент
вместе со своими опциями, поэтому потребители никогда не зашивают ключ в код.

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
