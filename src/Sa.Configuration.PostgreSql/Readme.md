# Sa.Configuration.PostgreSql

A dynamic configuration source for .NET that loads settings from PostgreSQL. Changes in the database are applied to the running application without restart — just call `Reload()` on `IConfigurationRoot`.

## Features

- **Live configuration**: values are stored in the database and can be changed at runtime
- **Parameterized SQL queries**: supports `@named_parameters` via `NpgsqlParameter` — parameters are cloned on every load, so one options instance is reusable
- **Automatic retry**: built-in retry strategy (`PgRetryStrategy`) with detection of Npgsql transient errors
- **Key/value trimming**: whitespace is automatically trimmed from both keys and values
- **Empty-value skipping**: keys or values that are `NULL`, empty, or whitespace-only are not added to the configuration

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

## Parameterized Queries

Use `@parameters` for filtering by client/tenant:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM client_settings WHERE client_id = @client_id",
    Parameters: [new NpgsqlParameter("client_id", "acme-corp")]
));
```

## Live Configuration Updates

When rows in the `app_settings` table change, the application can pick up new values:

```csharp
// After modifying rows in the database:
((IConfigurationRoot)app.Configuration).Reload();

// Or manually:
provider.Reload();  // DatabaseConfigurationProvider implements IConfigurationProvider
```

`Reload()` replaces the full set of keys: rows removed from the database disappear from the configuration, and new rows appear.

## Load Behavior

| Scenario | Result |
|----------|--------|
| Key is empty or whitespace only | Skipped |
| Value is `NULL` in DB | Skipped — the key is not added to the configuration |
| Value is an empty string or whitespace only | Skipped — the key is not added to the configuration |
| Key or value has surrounding whitespace | Trimmed |
| Connection/query error | `InvalidOperationException` with the original exception as `InnerException` |

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
    ('debug_mode', '');   -- skipped: not added to the configuration
```

## Dependencies

- `Microsoft.Extensions.Configuration`
- `Npgsql` (`NpgsqlParameter` is part of the public API)
- `Sa.Data.PostgreSql` (Npgsql wrapper with `PgRetryStrategy` and `IPgDataSource`)

## License

MIT
