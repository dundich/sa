# Sa.Configuration.PostgreSql

A dynamic configuration source for .NET that loads settings from PostgreSQL. Changes in the database are applied to the running application without restart — just call `Reload()` on `IConfigurationRoot`.

## Features

- **Live configuration**: values are stored in the database and can be changed at runtime
- **Parameterized SQL queries**: supports `@named_parameters` via `NpgsqlParameter` — parameters are cloned on every load, so one options instance is reusable
- **Automatic retry**: transient Npgsql failures (network errors, `08xxx` SQLSTATEs) are retried with decorrelated jitter back-off
- **One connection pool, not one per reload**: the pool is created once and reused by every `Reload()`
- **Async reload**: `LoadAsync(CancellationToken)` for timer- or `BackgroundService`-driven reloads
- **Key/value trimming**: whitespace is automatically trimmed from both keys and values
- **Layering control**: decide whether blank and `NULL` values delegate to lower-priority sources or override them

## Quick Start

```csharp
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "Host=localhost;Database=myapp;Username=app;Password=secret",
    SelectSql: "SELECT key, value FROM app_settings"
));

var app = builder.Build();

// Reading settings
var theme = app.Configuration["theme"];          // → "dark"
var lang  = app.Configuration["language"];       // → "en"
```

Options are validated when the source is registered, so a blank connection string or query
fails while the host is still building rather than at the first load.

## Parameterized Queries

Use `@parameters` for filtering by client/tenant:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM client_settings WHERE client_id = @client_id",
    Parameters: [new NpgsqlParameter("client_id", "acme-corp")]
));
```

## Reusing the Application's Connection Pool

By default the source owns a private data source. To share one pool with the rest of the
application, create it explicitly and pass it in:

```csharp
IPgDataSource appDataSource = IPgDataSource.Create(connectionString);

builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions(
        ConnectionString: string.Empty,   // ignored: the data source carries the connection
        SelectSql: "SELECT key, value FROM app_settings"),
    appDataSource);
```

If the application already registers `IPgDataSource` through `AddSaPostgreSqlDataSource`
(`o => o.WithConnectionString(...)`), resolve that instance and hand it to the configuration
source as well, so both paths share one pool.

The provider never disposes a data source it did not create — you keep ownership of
`appDataSource`. An owned data source is released when the `IConfigurationRoot` is disposed.

## Live Configuration Updates

When rows in the `app_settings` table change, the application can pick up new values:

```csharp
// After modifying rows in the database:
((IConfigurationRoot)app.Configuration).Reload();

// From a timer or BackgroundService, without blocking a thread:
await provider.LoadAsync(cancellationToken);
```

`Reload()` replaces the full set of keys: rows removed from the database disappear from the
configuration, and new rows appear. A **failed** load leaves the previous snapshot in place —
a transient database problem never wipes the running configuration.

`LoadAsync` is the async counterpart of `Load()` and additionally raises the provider's reload
token, so consumers holding the provider directly (rather than the `IConfigurationRoot`, which
raises its own) observe the new snapshot. Concurrent reloads collapse into a single query.

## Options

| Option | Default | Meaning |
|--------|---------|---------|
| `MaxAttempts` | `3` | Total load attempts including the first. `0` and `1` both mean a single attempt. |
| `MedianFirstRetryDelay` | `530` | Median first-retry delay in ms; each delay is jittered. The first retry is always immediate. |
| `SkipEmptyValues` | `true` | Drop rows with a blank key, or a `NULL`/blank value. See *Layering* below. |
| `LastWins` | `true` | For duplicate keys, the last row wins. Set to `false` for first-row-wins. |

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM app_settings") with
{
    MaxAttempts = 5,
    MedianFirstRetryDelay = 200,
    SkipEmptyValues = false,
    LastWins = true,
});
```

`ToString()` on the options is overridden: parameter values are counted rather than printed, and
the connection string's password is redacted, so options objects are safe to interpolate into logs.

## Load Behavior

| Scenario | Result |
|----------|--------|
| Key is `NULL`, empty, or whitespace only | Skipped |
| Value is `NULL` in DB | Skipped by default — see *Layering* |
| Value is an empty string or whitespace only | Skipped by default — see *Layering* |
| Key or value has surrounding whitespace | Trimmed |
| Query returns fewer than 2 columns | `InvalidOperationException` naming the actual column count |
| Transient connection error | Retried up to `MaxAttempts` |
| Permanent query error | Fails on the first attempt |
| Connection/query error after retries | `InvalidOperationException` with the original exception as `InnerException` |
| Load cancelled | `OperationCanceledException` — never wrapped |
| Duplicate keys in the result | Last row wins (`LastWins`) |

### Layering

`IConfiguration` resolves a key from the highest-priority source that has it. With
`SkipEmptyValues = true` (the default) a `NULL` or blank value in the database means this source
simply does not have the key, so a lower-priority source answers for it instead:

```csharp
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["feature_x"] = "fallback"
});
// database has feature_x = ''  →  configuration["feature_x"] == "fallback"
```

Set `SkipEmptyValues = false` to store those values instead, so this source overrides whatever
comes below it:

```csharp
// database has feature_x = ''  →  configuration["feature_x"] == ""
```

Note that a `NULL` value cannot shadow a lower-priority source: `ConfigurationRoot` keeps
scanning providers while they return `null`. Only an empty string overrides.

## Table Schema

Minimum table required for the provider:

```sql
CREATE TABLE app_settings (
    key   VARCHAR PRIMARY KEY,
    value TEXT
);

-- Sample data
INSERT INTO app_settings (key, value) VALUES
    ('theme',      'dark'),
    ('language',   'en'),
    ('debug_mode', '');   -- skipped by default: delegated to lower-priority sources
```

A `PRIMARY KEY` on `key` is not required — without it, duplicates are resolved by `LastWins`.

## Dependencies

- `Microsoft.Extensions.Configuration`
- `Npgsql` (`NpgsqlParameter` is part of the public API)
- `Sa.Data.PostgreSql` (Npgsql wrapper with `PgRetryStrategy` and `IPgDataSource`)

## License

MIT
