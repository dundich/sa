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

`AddSaS3BucketClient` takes the standard options callback, so configuration goes through `Configure` / `PostConfigure` / `Validate` like any other options type — not through a bespoke overload:

```csharp
services.AddSaS3BucketClient(o => o.Options(ob => ob.Configure(x =>
{
    x.Bucket = "mybucket";
    x.Endpoint = "http://localhost:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
    x.TotalRequestTimeout = TimeSpan.FromSeconds(180);
    x.ConnectionPoolLifetime = TimeSpan.FromMinutes(15);
    x.HandlerLifetime = Timeout.InfiniteTimeSpan; // or TimeSpan.FromHours(2) for periodic handler refresh
})));

// Usage:
var client = serviceProvider.GetRequiredService<IS3BucketClient>();
```

### From a configuration section

```csharp
// appsettings.json:
// { "S3": { "Endpoint": "http://localhost:9000", "AccessKey": "…", "SecretKey": "…", "Bucket": "mybucket" } }
services.AddSaS3BucketClient(b => b.FromConfiguration("S3"));
```

The section binds in a fixed slot **first**, so an `Options(...)` `Configure` has the last word. The `Options(...)` actions replay after the registration's own `PostConfigure` and `Validate`, so your checks add to the built-in ones instead of replacing them.

### Validation

Values are normalised (`PostConfigure`) and then validated on the way out, with `ValidateOnStart()` turning a bad configuration into an `OptionsValidationException` at host start rather than a bare `UriFormatException` from the middle of the first upload. The same checks are available outside DI:

```csharp
options.Validate(); // throws DataAnnotations.ValidationException
```

Register only one S3 bucket client per service collection — a second call throws `InvalidOperationException`, because both `Configure` callbacks would otherwise apply to the same unnamed options instance and the settings would silently merge.

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
