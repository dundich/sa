# Sa.Configuration.PostgreSql

A dynamic configuration source for .NET that loads settings from PostgreSQL. Changes in the database are applied to the running application without a restart — just call `Reload()` on `IConfigurationRoot`.

---

## Features

- **Live configuration** — values are stored in the database and can be changed at runtime.
- **Parameterized SQL queries** — supports `@named_parameters` via `NpgsqlParameter`. Parameters are cloned on every load, so a single options instance can be reused.
- **Automatic retries** — transient Npgsql errors (network failures, SQLSTATE `08xxx`) are retried with decorrelated jitter back-off.
- **One connection pool, not a new one on every reload** — the pool is created once and reused by all `Reload()` calls.
- **Asynchronous reload** — `LoadAsync(CancellationToken)` for timers and `BackgroundService`.
- **Key/value trimming** — whitespace is automatically trimmed from both keys and values.
- **`__` as `:` (optional)** — with `DoubleUnderscoreAsSeparator = true`, a double underscore in a key is read as the configuration separator, so `sa__pg__host` and `sa:pg:host` are the same setting. This is the same convention already used by `AddEnvironmentVariables()`. Disabled by default — keys are stored as-is.
- **Layer control** — decide whether empty and `NULL` values delegate to lower-priority sources or override them.

---

## Quick start

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

Options are validated when the source is registered, so an empty connection string or an empty query fails during host build rather than on the first load.

---

## Key separators

The configuration separator is `:`, so a nested setting is read as `configuration["sa:pg:host"]`.

By default, this source stores keys **as-is**. If a key legitimately contains `__` (or `:`), it is preserved unchanged — nothing is reformatted behind your back.

If the database is populated from the same places as environment variables (where the separator is `__`), you can enable the environment convention with `DoubleUnderscoreAsSeparator = true`. Then `__` in a key is read as `:`, exactly as `AddEnvironmentVariables()` does:

```sql
INSERT INTO app_settings (key, value) VALUES
    ('sa__pg__host', 'db.internal'),   -- read as sa:pg:host (alias enabled)
    ('sa:pg:port',   '5432');          -- read as sa:pg:port
```

The alias works in both directions: keys are stored in canonical form with `:`, but `configuration["sa__pg__host"]` also responds for `sa:pg:host`. A single underscore is data, not a separator (`config_db` remains `config_db`).

Two keys are considered the same setting after normalization, so if the query returns both records for the same path, they are duplicates, and `LastWins` decides which value survives.

The alias mirrors environment behavior, including its quirk: replacement is pairwise, so four underscores become two separators (`a____b` → `a::b`). With the default `DoubleUnderscoreAsSeparator = false`, the key is stored and read as-is.

---

## Key aliases

This package itself does not map keys. The explicit key alias feature — a C# path such as `sa:scope:api:auth:base_url` populated by the flat PHP key `auth_base_url` — lives in the Secrets layer **[Sa.Configuration](../Sa.Configuration)**: there the alias is applied to the **aggregate** configuration of *all* providers, not only this database. Set it there:

```csharp
using Sa.Configuration;

builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases =
        {
            ["sa:scope:api:auth:base_url"] = "auth_base_url",
            ["sa:scope:api:auth:token_ttl"] = "auth_token_ttl",
        },
    },
});
```

Because the alias is applied at the final post-secrets stage, it also resolves across sources — the flat key can come from this PostgreSQL source, while the canonical path can come from another. Full behavior is described in [README Sa.Configuration](../Sa.Configuration/Readme.md#key-aliases).

If this PostgreSQL source is registered **after** the alias pass and itself holds canonical paths, finalize the pipeline so the aliases land on top of it:

```csharp
builder.Configuration
    .AddSaPostgreSqlConfiguration(...)
    .AddSaConfiguration(...)
    .FinalizeSaConfiguration();
```

---

## Parameterized queries

Use `@parameters` to filter settings by client or tenant:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM client_settings WHERE client_id = @client_id",
    Parameters: [new NpgsqlParameter("client_id", "acme-corp")]
));
```

---

## Reusing the application connection pool

By default, the source owns its own data source. If you want to use one shared pool for the whole application, create it explicitly and pass it to the configuration source:

```csharp
IPgDataSource appDataSource = IPgDataSource.Create(connectionString);

builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions(
        ConnectionString: string.Empty,   // ignored: the connection comes from the data source
        SelectSql: "SELECT key, value FROM app_settings"),
    appDataSource);
```

If the application already registers `IPgDataSource` via `AddSaPostgreSqlDataSource` (`b => b.Options(o => o.Configure(x => x.ConnectionString = ...))`), get that instance and pass it to the configuration source — then both paths use the same connection pool.

The provider never disposes a data source it did not create — ownership of `appDataSource` remains yours. Its own source is disposed when `IConfigurationRoot` is disposed.

---

## Live configuration updates

When rows in the `app_settings` table are changed, the application can pick up the new values:

```csharp
// After changing rows in the database:
((IConfigurationRoot)app.Configuration).Reload();

// From a timer or BackgroundService, without blocking the thread:
await provider.LoadAsync(cancellationToken);
```

`Reload()` replaces the entire key set: rows removed from the database disappear from the configuration, and new ones appear. A **failed** load leaves the previous snapshot in place — a transient database problem never wipes out a working configuration.

`LoadAsync` is the asynchronous counterpart of `Load()`. It additionally raises the provider's reload token, so consumers that hold the provider itself (rather than `IConfigurationRoot`, which raises its own token) will see the new snapshot. Concurrent reloads collapse into a single request.

---

## Options

| Option | Default | Meaning |
|--------|---------|---------|
| `MaxAttempts` | `3` | Total number of load attempts, including the first one. `0` and `1` mean a single attempt |
| `MedianFirstRetryDelay` | `530` | Median delay of the first retry in ms; each delay is jittered. The first retry is always immediate |
| `SkipEmptyValues` | `true` | Skip rows whose **value** is `NULL` or empty. See *Layers* below. Empty **keys** are skipped regardless of this setting |
| `DoubleUnderscoreAsSeparator` | `false` | When `true`, read `__` in a key as the `:` separator, i.e. `sa__pg__host` ≡ `sa:pg:host`. `false` (default) — store `__` as-is |
| `LastWins` | `true` | For duplicate keys, the last row wins. `false` — the first one |

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM app_settings") with
{
    MaxAttempts = 5,
    MedianFirstRetryDelay = 200,
    SkipEmptyValues = false,
    DoubleUnderscoreAsSeparator = true,
    LastWins = true,
});
```

`ToString()` is overridden on options: parameter values are counted but not printed, and the password in the connection string is masked — so options are safe to interpolate into logs.

---

## Load behavior

| Scenario | Result |
|----------|--------|
| Key is `NULL` | Skipped — always, regardless of `SkipEmptyValues` |
| Key is empty or whitespace-only | Skipped — always, regardless of `SkipEmptyValues` |
| Value is `NULL` in the database | Skipped by default — see *Layers* |
| Value is an empty string or whitespace-only | Skipped by default — see *Layers* |
| Key or value has leading/trailing whitespace | Trimmed |
| Key contains `__` | Read as the `:` separator only when `DoubleUnderscoreAsSeparator = true`; by default — as-is |
| Query returns fewer than 2 columns | `InvalidOperationException` with the actual column count |
| Transient connection error | Retried up to `MaxAttempts` |
| Permanent query error | Fails on the first attempt |
| Error after all retries | `InvalidOperationException` with the original exception as `InnerException` |
| Load cancellation | `OperationCanceledException` — never wrapped |
| Duplicate keys in the result | The last row wins (`LastWins`) |

### Layers

`IConfiguration` takes a value from the highest-priority source that contains that key. With `SkipEmptyValues = true` (the default), a `NULL` or empty string value in the database means the source simply does not contain that key, and a lower-priority source is responsible for it:

```csharp
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["feature_x"] = "fallback"
});
// in the database feature_x = ''  →  configuration["feature_x"] == "fallback"
```

Set `SkipEmptyValues = false` to keep these values and override everything below:

```csharp
// in the database feature_x = ''  →  configuration["feature_x"] == ""
```

Note that a `NULL` value cannot override a lower-priority source: `ConfigurationRoot` continues traversing providers while they return `null`. Only an empty string overrides.

---

## Table schema

The minimal table required by the provider:

```sql
CREATE TABLE app_settings (
    key   VARCHAR PRIMARY KEY,
    value TEXT
);

-- Sample data
INSERT INTO app_settings (key, value) VALUES
    ('theme',      'dark'),
    ('language',   'en'),
    ('debug_mode', '');   -- skipped by default: delegates to lower sources
```

`PRIMARY KEY` on `key` is not required — without it, duplicates are resolved by the `LastWins` rule.

---

## Dependencies

- `Microsoft.Extensions.Configuration`
- `Npgsql` (`NpgsqlParameter` is part of the public API)
- `Sa.Data.PostgreSql` (Npgsql wrapper with `PgRetryStrategy` and `IPgDataSource`)

---

## License

MIT
