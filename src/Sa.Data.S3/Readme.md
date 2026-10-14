# Sa.Data.S3

Wrapper over `HttpClient` for S3-compatible storage systems (Minio, AWS S3, DigitalOcean Spaces, etc.) with a **fully self-implemented AWS Signature Version 4** — no dependencies on AWS SDK or Minio SDK.

---

## Features

- **Own AWS SigV4 implementation** — neither AWS SDK nor Minio SDK in the dependencies: just `HttpClient` and `Microsoft.Extensions.*`.
- **Low memory footprint** — roughly 150× lower than the Minio SDK and 17× lower than the AWS SDK, at speed comparable to AWS.
- **[Named clients](#named-clients)** — `AddSaS3BucketClient("name", ...)`: its own named `HttpClient` (connection pool, resilience, handler lifetime), its own settings and its own `IS3BucketClient` per name; resolve with `GetRequiredKeyedService<IS3BucketClient>("name")`.
- **[Configuration in appsettings](#configuration-in-appsettings)** — an `S3` section bound via `FromConfiguration`, with `Configure` / `PostConfigure` / `Validate` on top; a bad configuration fails the host at start-up rather than mid-request.
- **Files** — upload (including multipart uploads via `S3Upload`), streamed reads, listing by prefix with `List`, presigned links via `BuildFileUrl` / `GetFileUrl`, `IsFileExists`, `DeleteFile`.
- **Buckets** — `CreateBucket`, `DeleteBucket`, `IsBucketExists`.
- **Transport under control** — per-request timeout, connection-pool and handler lifetime, HTTP/2 — all in `S3BucketClientSetupOptions`.
- **`Sa.HybridFileStorage.S3` integration** — the `AddSaS3FileStorage` provider registers each folder's client with the same named registration.

---

## Quick Start

### Without DI

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

The options pipeline kicks in only with the DI registration; without it the transport is configured on the `HttpClient` itself (timeout, connection pool) and HTTP/2 via `S3BucketSettings.UseHttp2`.

### With DI

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

// Presigned link, valid for an hour; null when the file does not exist.
string? url = await client.GetFileUrl("docs/hello.txt", TimeSpan.FromHours(1), ct);
```

The registration name is the key to everything: the named `HttpClient`, the settings, the options instance and the client itself. Several clients per host and the naming rules — see [Named clients](#named-clients).

---

## Configuration in appsettings

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

`AddSaS3BucketClient` takes a single builder callback: `FromConfiguration` binds the section, `Options(...)` hands you the standard `OptionsBuilder<S3BucketClientSetupOptions>` — configuration goes through the ordinary options pipeline, no bespoke overloads.

- The section is an ordinary host configuration section: `appsettings.{Environment}.json`, environment variables (`S3__Bucket=mybucket`) and the whole `IConfiguration` chain work as usual. `FromConfiguration` may be called several times — the last path wins.
- The section binds in a fixed slot **first**, so an `Options(...)` `Configure` has the last word — wherever those calls sit in the callback. The `Options(...)` actions replay after the registration's own `PostConfigure` and `Validate`, so your checks add to the built-in ones instead of replacing them.
- **Validation is on by default**: the registration itself calls `ValidateOnStart()`, so a bad section yields an `OptionsValidationException` at host start rather than a bare `UriFormatException` from the middle of the first upload. Extra checks are added via `Validate(...)` in `Options(...)`; the same checks are available outside DI:

```csharp
options.Validate(); // throws DataAnnotations.ValidationException
```

---

## Named clients

Every registration is keyed by its name, and the name must be unique in the service collection: a second `AddSaS3BucketClient` call **under the same name** throws `InvalidOperationException`, because the second `Configure` would stack on the same named options instance and a second keyed client under the same key would make resolution ambiguous. Different names, however, are fully independent — each gets its own named `HttpClient` (connection pool, resilience, handler lifetime), its own keyed `S3BucketSettings`, its own keyed `IS3BucketClient` and its own named options instance:

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

Two clients never share credentials, timeouts or a connection pool. The HTTP wiring underneath is shared code exposed internally as `AddSaS3BucketClientCore(services, clientName, settingsFactory)`.

Resolving the client — keyed by the registration name; there are no unkeyed aliases, so an unkeyed `GetRequiredService<IS3BucketClient>()` returns `null`:

```csharp
IS3BucketClient ordersClient = serviceProvider.GetRequiredKeyedService<IS3BucketClient>("orders");
S3BucketSettings ordersSettings = serviceProvider.GetRequiredKeyedService<S3BucketSettings>("orders");
```

The S3 provider of `Sa.HybridFileStorage.S3` (`AddSaS3FileStorage`, call it once per storage) keys each storage's client by the registration's name the same way, so two storages — even two with the same endpoint — stay isolated, and a read that misses the first bucket continues to the next one of the same storage type. For `AddSaS3FileStorage` the registration name is an internal detail — the storage resolves its own client together with its options, so consumers never hardcode the key.

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

`TotalRequestTimeout`, `ConnectionPoolLifetime` and `HandlerLifetime` belong to the named `HttpClient` of the DI registration; when creating a client by hand, the timeout is set on the `HttpClient` itself.

---

## Motivation

This is a fork of [teoadal/Storage](https://github.com/teoadal/Storage). The motivation was memory: the [AWS SDK for .NET](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/welcome.html) (4.x) and [Minio .NET](https://github.com/minio/minio-dotnet) (6.x) clients consumed too much of it. The package keeps the idea of a lean `HttpClient`-only client; the speed and memory benchmark results are listed under [Features](#features).

---

## License

MIT
