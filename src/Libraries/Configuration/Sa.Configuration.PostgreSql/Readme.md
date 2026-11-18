# Sa.Configuration.PostgreSql

**English** | [Русский](Readme-ru.md)

A configuration source that reads settings from a PostgreSQL table. Values can be changed right in the database, and the application picks them up via `ReloadDynamicAsync()` — no redeploy, no restart.

The package complements **[Sa.Configuration](../Sa.Configuration)**: secrets in placeholders, key aliases and per-client overlays come from the base package; this one adds the data source and its options.

## Contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [Connection strategies](#connection-strategies)
- [Keys and separators](#keys-and-separators)
- [Key aliases](#key-aliases)
- [Parameterized queries](#parameterized-queries)
- [Clients](#clients)
- [Live configuration updates](#live-configuration-updates)
- [Details](#details)
- [License](#license)

## Installation

```bash
dotnet add package Sa.Configuration.PostgreSql
```

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

The short form `AddSaPostgreSqlConfiguration(options)` uses `PgConfigurationConnection.FromServices()`: the application's already registered `IPgDataSource` (or `NpgsqlDataSource`) is reused, so no second pool is created.

Options are validated when the source is registered, so a blank query fails during host build rather than on the first load. The connection is validated by its own strategy (`ConnectionString(...)` parses it eagerly, `DataSource(...)` rejects `null`).

## Connection strategies

`PgConfigurationConnection` has three shapes — the same "where does the data come from" question the rest of `Sa.Data.PostgreSql` answers:

| Strategy | Ownership | When the connection is available |
|----------|-----------|----------------------------------|
| `ConnectionString("…")` | the source creates and disposes its own pool | immediately, at registration |
| `DataSource(IPgDataSource)` | the caller owns it | immediately, at registration |
| `FromServices("key"?)` — **the default** | the application pool (`PgDataSourceResolver`: keyed `IPgDataSource` → plain `IPgDataSource` → `NpgsqlDataSource`) | from the first runtime initialization |

A shared application pool:

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

`FromServices` is **late binding**: configuration is assembled before the container exists, so the source resolves on the first runtime initialization. Until then the database layer serves no keys, which means such a source must be initialized before the first request (explicitly, or with the host hook — see `SaDynamicInitialLoad`). If values are needed while the `IServiceCollection` is still being built, use `ConnectionString`/`DataSource`. `AsOptional()` turns an unresolvable source into an empty layer instead of a failed startup.

## Keys and separators

The configuration separator is `:`, so a nested setting is read as `configuration["sa:pg:host"]`.

By default the source stores keys **as-is** and reformats nothing. If the database is populated from the same places as environment variables (where the separator is `__`), enable the environment convention:

```csharp
var options = new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings")
{
    DoubleUnderscoreAsSeparator = true,
};
```

```sql
INSERT INTO app_settings (key, value) VALUES
    ('sa__pg__host', 'db.internal'),   -- read as sa:pg:host
    ('sa:pg:port',   '5432');          -- read as sa:pg:port
```

The alias works in both directions: keys are stored in canonical form with `:`, but `configuration["sa__pg__host"]` also responds. A single underscore is data, not a separator (`config_db` remains `config_db`). Replacement is pairwise, like in `AddEnvironmentVariables()`: `a____b` becomes `a::b`.

After normalization two keys may mean one setting. If the query returns both records for the same path, they are duplicates, and `LastWins` decides which value survives.

## Key aliases

This package itself does not map keys. Explicit aliases — the link "C# path `sa:scope:api:auth:base_url` ← flat `auth_base_url`" — live in the Secrets layer of **[Sa.Configuration](../Sa.Configuration)** and apply to the aggregate configuration of **all** providers:

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

If this PostgreSQL source is registered **after** the alias pass and stores canonical paths itself, finalize the pipeline so the aliases are laid over it:

```csharp
builder.Configuration
    .AddSaPostgreSqlConfiguration(...)
    .AddSaConfiguration(...)
    .FinalizeSaConfiguration();
```

Full alias behaviour — in [Sa.Configuration](../Sa.Configuration/Readme.md#aliases-full-rules).

## Parameterized queries

Use `@parameters` to filter settings by client or tenant:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM client_settings WHERE client_id = @client_id",
    Parameters: [new NpgsqlParameter("client_id", "acme-corp")]
));
```

Columns are read by position: the query must return **exactly two** columns, `(key, value)`. Anything else is a startup error, including the three-column `(client, key, value)` shape: read as `(key, value)` one column would silently shift into another. Parameters are cloned on every load, so a single options instance can be reused.

## Clients

The core reads the shared values; each client reads its overrides from a similar table filtered by `client_id`. Key names in both tables are **the same** — no prefix, no shared dictionary — so one client's values cannot leak into the core or into another client.

```sql
CREATE TABLE setting (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

INSERT INTO setting (key, value) VALUES
    ('s3_url',   'A'),
    ('theme',    'dark'),
    ('language', 'en');

CREATE TABLE client_settings (
    client_id TEXT NOT NULL,
    key       TEXT NOT NULL,
    value     TEXT NOT NULL,
    PRIMARY KEY (client_id, key)
);

INSERT INTO client_settings (client_id, key, value) VALUES
    ('client1', 's3_url', 'B1'),
    ('client1', 'theme',  'light'),
    ('client2', 's3_url', 'C'),
    ('client2', 'language', 'de');
```

The core is an ordinary two-column query:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.FromServices(),
    new PostgreSqlConfigurationOptions("SELECT key, value FROM setting"));
```

The client layer is one query returning `(client_id, key, value)` for **all** clients at once; the registry groups rows by `client_id` and builds a root per client. There is no discovery step and no per-client query.

```csharp
using Sa.Configuration.Clients;
using Sa.Data.PostgreSql;

public sealed class PgClientSettingsStore(IPgDataSource dataSource)
{
    public async ValueTask<IReadOnlyList<ClientSetting>> LoadAllAsync(CancellationToken ct)
    {
        List<ClientSetting> rows = [];

        await dataSource.ExecuteReaderAsync(
            "SELECT client_id, key, value FROM client_settings",
            (reader, _) => rows.Add(new ClientSetting(
                reader.GetString(0), reader.GetString(1), reader.GetString(2))),
            initCommand: null,
            ct);

        return rows;
    }
}

builder.Services.AddSingleton<PgClientSettingsStore>();

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    // One dependency from DI (plus the token) → rows for all clients.
    clients.Add<PgClientSettingsStore>((store, ct) => store.LoadAllAsync(ct));
});
```

```csharp
IConfiguration core = app.Configuration;

core["s3_url"];                                       // "A"
clients.GetClient("client1")["s3_url"];               // "B1"
clients.GetClient("client2")["s3_url"];               // "C"
clients.GetClient("client1")["language"];             // "en" — core fallback
clients.Clients;                                      // ["client1", "client2"]
```

The store is called lazily on the first access and again on every `ReloadDynamicAsync()`, so a row inserted into `client_settings` appears without a restart. A separate pool or another database is just a different data source that the store resolves.

Overlay rules, keyed injection and attaching/removing clients at runtime — in [Sa.Configuration](../Sa.Configuration/Readme.md#clients-full-rules).

## Live configuration updates

When rows in the table change, the application picks up the new values like this:

```csharp
// Refresh only the database layer — files on disk are not re-read, while aliases/placeholders
// are recomputed from the fresh values:
await app.Services.GetRequiredService<ISaConfigurationReloader>()
    .ReloadDynamicAsync(cancellationToken);

// Full reload (all providers, files included):
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

`Reload()` replaces the whole key set: rows deleted from the database disappear, new ones appear. A **failed** load keeps the previous snapshot — a temporary database problem never blanks a working configuration.

`LoadAsync` is the asynchronous counterpart of `Load()`. It additionally raises the provider's reload token, so consumers that hold the provider itself (rather than an `IConfigurationRoot`) see the new snapshot. Concurrent reloads collapse into a single query.

## Details

### Options

| Option | Default | Meaning |
|--------|---------|---------|
| `MaxAttempts` | `3` | Total load attempts, including the first. `0` and `1` mean a single attempt |
| `MedianFirstRetryDelay` | `530` | Median first-retry delay in ms; every delay is jittered. The first retry is always immediate |
| `SkipEmptyValues` | `true` | Drop rows whose **value** is `NULL` or empty. See *Layers*. Empty **keys** are dropped regardless of this setting |
| `DoubleUnderscoreAsSeparator` | `false` | With `true`, read `__` in a key as the `:` separator, i.e. `sa__pg__host` ≡ `sa:pg:host` |
| `LastWins` | `true` | On duplicate keys the last row wins. `false` — the first one |

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

Transient Npgsql errors (network failures, SQLSTATE `08xxx`) are retried with decorrelated jitter back-off. The connection pool is created once and reused by all `Reload()` calls rather than rebuilt on every load.

`ToString()` is overridden on both the options and the connection strategy: parameter values are counted but not printed, and the password in the connection string is masked. Both are therefore safe to interpolate into logs.

### Load behavior

| Situation | Result |
|-----------|--------|
| Key `NULL` | Skipped — always, regardless of `SkipEmptyValues` |
| Key empty or whitespace-only | Skipped — always, regardless of `SkipEmptyValues` |
| Value `NULL` in the database | Skipped by default — see *Layers* |
| Value is an empty or whitespace-only string | Skipped by default — see *Layers* |
| Leading/trailing whitespace in a key or value | Trimmed |
| Key contains `__` | Read as the `:` separator only with `DoubleUnderscoreAsSeparator = true` |
| Query returned fewer than 2 columns | `InvalidOperationException` naming the actual column count |
| Transient connection error | Retried up to `MaxAttempts` |
| Permanent query error | Fails on the first attempt |
| Error after all retries | `InvalidOperationException` with the original exception in `InnerException` |
| Load cancellation | `OperationCanceledException` — never wrapped |
| Duplicate keys in the result | The last row wins (`LastWins`) |

### Layers

`IConfiguration` takes a value from the highest-priority source that contains the key. With `SkipEmptyValues = true` (the default) a `NULL` or empty value in the database means the source simply does not contain that key, and a lower-priority source answers for it:

```csharp
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["feature_x"] = "fallback"
});
// in the database feature_x = ''  →  configuration["feature_x"] == "fallback"
```

Set `SkipEmptyValues = false` to keep those values and override everything below:

```csharp
// in the database feature_x = ''  →  configuration["feature_x"] == ""
```

A `NULL` value cannot override a lower-priority source, because `ConfigurationRoot` keeps walking providers while they return `null`. Only an empty string overrides.

### Table schema

The minimal table the provider needs:

```sql
CREATE TABLE app_settings (
    key   VARCHAR PRIMARY KEY,
    value TEXT
);

-- Sample data
INSERT INTO app_settings (key, value) VALUES
    ('theme',      'dark'),
    ('language',   'en'),
    ('debug_mode', '');   -- skipped by default: delegated to lower sources
```

`PRIMARY KEY` on `key` is optional — without it duplicates are resolved by `LastWins`.

### Dependencies

- `Microsoft.Extensions.Configuration`
- `Npgsql` (`NpgsqlParameter` is part of the public API)
- `Sa.Data.PostgreSql` (the Npgsql wrapper with `PgRetryStrategy` and `IPgDataSource`)
- `Sa.Configuration` (the dynamic-source marker, runtime/reloader and the client configuration registry)

## License

MIT
