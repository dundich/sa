# Sa.Data.S3

Wrapper over `HttpClient` for working with S3-compatible storage systems (Minio, AWS S3, DigitalOcean Spaces, etc.). Fully self-implemented AWS Signature Version 4 — **no dependencies on AWS SDK or Minio SDK**.

---

## Motivation

This is a fork of https://github.com/teoadal/Storage. Motivation: the [AWS SDK for .NET](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/welcome.html) (4.x) and [Minio .NET](https://github.com/minio/minio-dotnet) (6.x) clients consumed too much memory. Result: speed is comparable to AWS, while memory consumption is ~150× lower than Minio SDK and ~17× lower than AWS SDK.

---

## Creating a Client

### Without DI

```csharp
var client = new S3BucketClient(new HttpClient(), new S3BucketClientSetupOptions
{
    Bucket = "mybucket",
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123"
});
```

### With DI

`AddSaS3BucketClient(name, ...)` takes the standard options callback, so configuration goes through `Configure` / `PostConfigure` / `Validate` like any other options type — not through a bespoke overload. The name identifies the client everywhere: it keys the named `HttpClient` (connection pool, resilience, handler lifetime), the settings, the options instance and the client itself — and it is what you resolve by:

```csharp
services.AddSaS3BucketClient("my-client", o => o.Options(ob => ob.Configure(x =>
{
    x.Bucket = "mybucket";
    x.Endpoint = "http://localhost:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
    x.TotalRequestTimeout = TimeSpan.FromSeconds(180);
    x.ConnectionPoolLifetime = TimeSpan.FromMinutes(15);
    x.HandlerLifetime = Timeout.InfiniteTimeSpan; // or TimeSpan.FromHours(2) for periodic handler refresh
})));

// Usage — resolve by the registration name:
var client = serviceProvider.GetRequiredKeyedService<IS3BucketClient>("my-client");
```

### From a configuration section

```csharp
// appsettings.json:
// { "S3": { "Endpoint": "http://localhost:9000", "AccessKey": "…", "SecretKey": "…", "Bucket": "mybucket" } }
services.AddSaS3BucketClient("my-client", b => b.FromConfiguration("S3"));
```

The section binds in a fixed slot **first**, so an `Options(...)` `Configure` has the last word. The `Options(...)` actions replay after the registration's own `PostConfigure` and `Validate`, so your checks add to the built-in ones instead of replacing them.

### Validation

Values are normalised (`PostConfigure`) and then validated on the way out, with `ValidateOnStart()` turning a bad configuration into an `OptionsValidationException` at host start rather than a bare `UriFormatException` from the middle of the first upload. The same checks are available outside DI:

```csharp
options.Validate(); // throws DataAnnotations.ValidationException
```

### One registration per name, many named clients per host

Every registration is keyed by its name, and the name must be unique in the service collection: a
second `AddSaS3BucketClient` call **under the same name** throws `InvalidOperationException`, because
the second `Configure` would stack on the same named options instance and a second keyed client
under the same key would make resolution ambiguous. Different names, however, are fully independent —
each gets its own named `HttpClient` (connection pool, resilience, handler lifetime), its own keyed
`S3BucketSettings`, its own keyed `IS3BucketClient` and its own named options instance:

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

Two clients never share credentials, timeouts or a connection pool. The HTTP wiring underneath is
shared code exposed internally as `AddSaS3BucketClientCore(services, clientName, settingsFactory)`.
The S3 provider of `Sa.HybridFileStorage.S3` (`AddSaS3FileStorage`, call it once per storage) keys
each storage's client by the registration's name the same way, so two storages — even two with the
same endpoint — stay isolated, and a read that misses the first bucket continues to the next one of
the same storage type.

Resolving the client — keyed by the registration name; there are no unkeyed aliases, so an unkeyed
`GetRequiredService<IS3BucketClient>()` returns `null`:

```csharp
IS3BucketClient ordersClient = serviceProvider.GetRequiredKeyedService<IS3BucketClient>("orders");
S3BucketSettings ordersSettings = serviceProvider.GetRequiredKeyedService<S3BucketSettings>("orders");
```

For `AddSaS3FileStorage` the registration name is an internal detail — the storage resolves its own
client together with its options, so consumers never hardcode the key.

---

## Settings

`S3BucketClientSetupOptions` inherits `S3BucketSettings`, so the very same object is handed to `S3BucketClient` — there is no projection between them that could quietly drop a property.

| Property | Description | Default |
|----------|-------------|---------|
| `AccessKey` | S3 access key | *(required)* |
| `SecretKey` | S3 secret key | *(required)* |
| `Bucket` | Bucket name | *(required)* |
| `Endpoint` | S3 storage URL (absolute http/https) | *(required)* |
| `Region` | Region for SigV4 | `"us-east-1"` |
| `Service` | Service name for SigV4 | `"s3"` |
| `UseHttp2` | Force HTTP/2 | `false` |
| `TotalRequestTimeout` | Per-request timeout | `180 sec` |
| `ConnectionPoolLifetime` | Connection pool lifetime | `15 min` |
| `HandlerLifetime` | HttpClient handler lifetime | `∞` (infinite) |
