# Sa.Configuration.PostgreSql

**English** | [Русский](Readme-ru.md)

A dynamic configuration source for .NET that loads settings from PostgreSQL. Changes in the database are applied to the running application without a restart — just call `Reload()` on `IConfigurationRoot`.

## Installation

```bash
dotnet add package Sa.Configuration.PostgreSql
```

---

## Contents

- [Installation](#installation)
- [Features](#features)
- [Quick start](#quick-start)
- [Key separators](#key-separators)
- [Key aliases](#key-aliases)
- [Connection strategies](#connection-strategies)
- [Parameterized queries](#parameterized-queries)
- [Clients](#clients)
- [Live configuration updates](#live-configuration-updates)
- [Options](#options)
- [Load behavior](#load-behavior)
  - [Layers](#layers)
- [Table schema](#table-schema)
- [Dependencies](#dependencies)
- [License](#license)

## Features

- **Live configuration** — values are stored in the database and can be changed at runtime, and the source is reloaded with `ISaConfigurationReloader.ReloadDynamicAsync()` (see [Sa.Configuration](../Sa.Configuration/Readme.md#dynamic-reload)).
- **Parameterized SQL queries** — supports `@named_parameters` via `NpgsqlParameter`. Parameters are cloned on every load, so a single options instance can be reused.
- **Automatic retries** — transient Npgsql errors (network failures, SQLSTATE `08xxx`) are retried with decorrelated jitter back-off.
- **One connection pool, not a new one on every reload** — the pool is created once and reused by all `Reload()` calls.
- **Asynchronous reload** — `LoadAsync(CancellationToken)` for timers and `BackgroundService`.
- **Key/value trimming** — whitespace is automatically trimmed from both keys and values.
- **`__` as `:` (optional)** — with `DoubleUnderscoreAsSeparator = true`, a double underscore in a key is read as the configuration separator, so `sa__pg__host` and `sa:pg:host` are the same setting. This is the same convention already used by `AddEnvironmentVariables()`. Disabled by default — keys are stored as-is.
- **Layer control** — decide whether empty and `NULL` values delegate to lower-priority sources or override them.
- **Connection strategies** — a private pool, a shared `IPgDataSource`, or the application's own data source resolved from DI (`FromServices`, the default). No connection string is required when the application already has one.
- **Clients in one query** — a three-column `(client, key, value)` query maps rows to `sa:clients:{client}:{key}`, so every client is available at once and a new one appears after a reload without a restart.
- **Dynamic by default** — the source implements `ISaDynamicConfigurationSource`; `ISaConfigurationReloader` reloads it (and the alias/placeholder pass) without re-reading the files.

---

## Quick start

```csharp
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// The connection strategy says where the data comes from; the options say what to read.
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.ConnectionString("Host=localhost;Database=myapp;Username=app;Password=secret"),
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));

var app = builder.Build();

// Reading settings
var theme = app.Configuration["theme"];          // → "dark"
var lang  = app.Configuration["language"];       // → "en"
```

The short form `AddSaPostgreSqlConfiguration(options)` uses
`PgConfigurationConnection.FromServices()`: the application's already registered `IPgDataSource`
(or `NpgsqlDataSource`) is reused, so no second pool is created.

Options are validated when the source is registered, so a blank query fails during host build rather
than on the first load. The connection is validated by its own strategy (`ConnectionString(...)`
parses it eagerly, `DataSource(...)` rejects `null`).

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

## Connection strategies

`PgConfigurationConnection` has three forms — the same "where the data comes from" question the rest
of `Sa.Data.PostgreSql` answers:

| Strategy | Ownership | When the connection is available |
|----------|-----------|----------------------------------|
| `ConnectionString("…")` | the source creates and disposes its own pool | immediately, at registration |
| `DataSource(IPgDataSource)` | the caller keeps ownership | immediately, at registration |
| `FromServices("key"?)` — **default** | the application's pool (`PgDataSourceResolver`: keyed `IPgDataSource` → default `IPgDataSource` → `NpgsqlDataSource`) | from the first runtime initialization |

Sharing the application's pool:

```csharp
IPgDataSource appDataSource = IPgDataSource.Create(connectionString);

builder.Configuration.AddSaPostgreSqlConfiguration(
    appDataSource,
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
```

Or let the source take the registered one — nothing is created and nothing is disposed:

```csharp
builder.Services.AddSaPostgreSqlDataSource(b => b.FromConfiguration("Postgres"));

builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));

builder.Services.AddSaConfigurationRuntime(builder.Configuration);

var app = builder.Build();
await app.Services.GetRequiredService<ISaConfigurationReloader>()
    .EnsureInitializedAsync(app.Lifetime.ApplicationStopping);
```

`FromServices` is **late-bound**: configuration is assembled before the container exists, so the data
source is resolved during the first runtime initialization (explicitly, or by the hosted hook — see
`SaDynamicInitialLoad`). Until then the database layer contributes no keys, so a source in that mode
should be initialized before the first request. Use `ConnectionString`/`DataSource` when the values
are needed while the service collection is still being built. `AsOptional()` turns an unresolvable
source into an empty layer instead of a failed start.

---

## Clients

Give the query a client column and one result set serves every client: rows with `NULL` in that
column are the shared base values, the rest land under `sa:clients:{client}:{key}`.

```sql
CREATE TABLE settings (
    client_id TEXT,
    key       TEXT NOT NULL,
    value     TEXT NOT NULL
);

INSERT INTO settings (client_id, key, value) VALUES
    (NULL,   'theme',    'dark'),    -- base value for everyone
    (NULL,   'language', 'en'),
    ('acme', 'theme',    'light'),   -- acme overrides only the theme
    ('beta', 'language', 'de');      -- beta overrides only the language
```

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.FromServices(),
    new PostgreSqlConfigurationOptions("SELECT client_id, key, value FROM settings")
    {
        ClientColumn = "client_id",      // must be the first column
    });
```

```csharp
using Sa.Configuration.Clients;

configuration.GetClientNames();                     // ["acme", "beta"]
configuration.ForClient("acme")["theme"];           // "light"
configuration.ForClient("acme")["language"];        // "en" — base value
configuration["sa:clients:acme:theme"];             // "light" — the raw prefixed key
```

A filtered query works too — the constant client name tells the source which prefix to use:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    connectionString,
    new PostgreSqlConfigurationOptions(
        "SELECT key, value FROM settings WHERE client_id = @client_id",
        new NpgsqlParameter("client_id", "acme"))
    {
        Client = "acme",
    });
```

Prefer the single query: a filtered source is fixed at build time, so a client added to the database
would need a restart. With `ClientColumn`, `ReloadDynamicAsync()` picks up the new client at once.
See [Sa.Configuration](../Sa.Configuration/Readme.md#clients-keyed-configuration) for the overlay,
computed keys, aliases and keyed DI.

---

## Live configuration updates

When rows in the `app_settings` table are changed, the application can pick up the new values:

```csharp
// Refresh the database layer only — files on disk are not re-read, and the aliases/placeholders
// are recomputed from the fresh values:
await app.Services.GetRequiredService<ISaConfigurationReloader>()
    .ReloadDynamicAsync(cancellationToken);

// Full reload, as before (every provider, including the files):
((IConfigurationRoot)app.Configuration).Reload();
```

Periodic polling is opt-in and registers exactly one hosted service:

```csharp
builder.Services.AddSaConfigurationRuntime(builder.Configuration, o =>
{
    o.DynamicReload = true;
    o.DynamicReloadInterval = TimeSpan.FromSeconds(15);
});
```

`Reload()` replaces the entire key set: rows removed from the database disappear from the configuration, and new ones appear. A **failed** load leaves the previous snapshot in place — a transient database problem never wipes out a working configuration.

`LoadAsync` is the asynchronous counterpart of `Load()`. It additionally raises the provider's reload token, so consumers that hold the provider itself (rather than `IConfigurationRoot`, which raises its own token) will see the new snapshot. Concurrent reloads collapse into a single request.

---

## Options

| Option | Default | Meaning |
|--------|---------|---------|
| `Client` | `null` | Constant client name: stores every key under `{ClientsSection}:{Client}:{key}`. For a query already filtered by client. Mutually exclusive with `ClientColumn` |
| `ClientColumn` | `null` | Column that carries the client name (`client, key, value`). Rows with a `NULL`/empty client stay base values |
| `ClientsSection` | `"sa:clients"` | Section the client overrides are stored under |
| `MaxAttempts` | `3` | Total number of load attempts, including the first one. `0` and `1` mean a single attempt |
| `MedianFirstRetryDelay` | `530` | Median delay of the first retry in ms; each delay is jittered. The first retry is always immediate |
| `SkipEmptyValues` | `true` | Skip rows whose **value** is `NULL` or empty. See *Layers* below. Empty **keys** are skipped regardless of this setting |
| `DoubleUnderscoreAsSeparator` | `false` | When `true`, read `__` in a key as the `:` separator, i.e. `sa__pg__host` ≡ `sa:pg:host`. `false` (default) — store `__` as-is |
| `LastWins` | `true` | For duplicate keys, the last row wins. `false` — the first one |

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.ConnectionString("..."),
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings") with
    {
        MaxAttempts = 5,
        MedianFirstRetryDelay = 200,
        SkipEmptyValues = false,
        DoubleUnderscoreAsSeparator = true,
        LastWins = true,
    });
```

`ToString()` is overridden on options and on the connection strategy: parameter values are counted but never printed, and a connection string's password is masked — so both are safe to interpolate into logs.

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
- `Sa.Configuration` (the dynamic-source marker, the runtime/reloader and the per-client overlay)

---

## License

MIT
