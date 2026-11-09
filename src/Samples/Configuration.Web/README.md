# Configuration.Web

**English** | [Русский](Readme-ru.md)

ASP.NET Core Minimal API sample demonstrating **Sa.Configuration** — CLI argument parsing, secure secrets management from files/environment variables with `{{placeholder}}` substitution, and dynamic PostgreSQL-backed configuration.

---

## Contents

- [Quick Start](#quick-start)
- [What This Sample Demonstrates](#what-this-sample-demonstrates)
- [Configuration Chain](#configuration-chain)
  - [Encrypted secrets layer (demo)](#encrypted-secrets-layer-demo)
  - [Example: secrets.txt](#example-secretstxt)
  - [Example: appsettings.json with Placeholders](#example-appsettingsjson-with-placeholders)
- [Key Code](#key-code)
  - [Program.cs](#programcs)
  - [Expected Response](#expected-response)
- [Dependencies](#dependencies)
- [License](#license)

## Quick Start

```bash
# 1. Start PostgreSQL (or use the shared Samples docker-compose)
cd src/Samples
docker compose up db -d

# 2. Run the sample
dotnet run --project Samples/Configuration.Web
```

Open `http://localhost:5245/settings` in your browser to see all configuration values loaded from three sources: CLI arguments, secrets file, and PostgreSQL database.

---

## What This Sample Demonstrates

1. **Secrets Management** — `{{sa_secret}}` placeholders in `appsettings.json` are resolved from a chain of stores: CLI arguments → environment variables → `.env` → encrypted files → plaintext files. This sample commits an AES-256-GCM-encrypted `secrets.lsr.Development.pub`.
2. **Dynamic Configuration from PostgreSQL** — Application settings live in a DB table and are reflected in-app without a restart; `POST /settings/reload` refreshes only that layer.
3. **Per-client overrides** — the same table carries `client_id`; `GET /settings/{client}` lays `acme`'s or `beta`'s rows over the shared ones, and no background service is registered.
4. **CLI Argument Parsing** — The `Arguments` class is wired through `AddSaConfiguration()` for command-line secret overrides.
5. **Zero ORM** — No EF Core or migrations. Just raw Npgsql with `CREATE TABLE IF NOT EXISTS`.

---

## Configuration Chain

The placeholder format `{{key}}` resolves values from the chained secret stores in order:

1. **CLI Arguments** — e.g., `/sa_secret:"Override from CLI"`
2. **Environment Variables** — e.g., `SA_SECRET="My Secret"`
3. **`.env.{env}` / `.env`** — local overrides (the key can also live here)
4. **`secrets.{client}.{env}.pub` / `secrets.{client}.pub`** — committed encrypted secrets
5. **`secrets.{env}.txt` / `secrets.txt`** — plaintext files (`key=value`, `#` comments, quotes trimmed)

Optional variant `{{?key}}` returns `null` instead of throwing if the key is missing.

### Encrypted secrets layer (demo)

This sample commits `secrets.lsr.Development.pub` on purpose. It is encrypted with
`AES-256-GCM` and overrides `sa_secret` from `secrets.txt`. The decryption key is public:
`demo-only-not-for-production` (see `.env.example` and `Properties/launchSettings.json`).

> **DEMO ONLY.** A real project keeps the key in CI/CD or a secret manager and never commits it.
> Encrypted files themselves are safe to commit.

Encrypt/decrypt/check with the repository script:

```bash
# Edit secrets.txt, then re-encrypt it for client "lsr" and environment "Development"
SA_SECRETS_DECRYPTION_KEY=demo-only-not-for-production \
  ./build-sh/do-secrets.sh encrypt src/Samples/Configuration.Web/secrets.txt \
  --client lsr --env Development \
  -o src/Samples/Configuration.Web/secrets.lsr.Development.pub

# Verify every encrypted file decrypts with the available key
./build-sh/do-secrets.sh check src/Samples/Configuration.Web/secrets*.pub
```

The decrypted value of `sa_secret` is shown on `/settings` as `ENCRYPTED DEMO SECRET`.

### Example: secrets.txt

```
sa_pg_host=localhost
sa_pg_user=postgres
sa_pg_password=postgres
sa_pg_port=5432
sa_pg_database=postgres
sa_pg_schema=public

sa_secret= "TOP SECRET!"
```

### Example: appsettings.json with Placeholders

```json
{
  "secret": "{{sa_secret}}",
  "sa": {
    "pg": {
      "connection": "User ID={{sa_pg_user}};Password={{sa_pg_password}};Host={{sa_pg_host}}"
    }
  }
}
```

---

## Key Code

### Program.cs

```csharp
var builder = WebApplication.CreateSlimBuilder(args);

// Static layer: the host already added appsettings/env/args. Sa adds the secret pass and the
// per-client overlay.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets();
    sa.UseClients();
});

const string PG_KEY = "sa:pg:connection";
string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

using var ds = IPgDataSource.Create(connectionString);

// One table for everything: client_id NULL holds the base values, a non-null client_id holds that
// client's overrides.
ds.ExecuteScalar("""
    CREATE TABLE IF NOT EXISTS settings (
        client_id TEXT,
        key       TEXT NOT NULL,
        value     TEXT NOT NULL
    );

    CREATE UNIQUE INDEX IF NOT EXISTS settings_client_key
        ON settings ((COALESCE(client_id, '')), key);

    INSERT INTO settings (client_id, key, value) VALUES
        (NULL,   'theme',         'dark'),
        (NULL,   'language',      'en'),
        (NULL,   'notifications', 'enabled'),
        ('acme', 'theme',         'light'),
        ('acme', 'notifications', 'disabled'),
        ('beta', 'language',      'de')
    ON CONFLICT DO NOTHING;
""", null).Wait();

// Dynamic layer: the application's own pool, so no second connection pool is opened, and the
// explicit strategy means the values are available immediately.
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.DataSource(ds),
    new PostgreSqlConfigurationOptions(
        // Column order is part of the contract: client, key, value.
        SelectSql: "SELECT client_id, key, value FROM settings")
    {
        ClientColumn = "client_id",
    });

// Runtime: reload on demand (no background timer by default) and a keyed IConfiguration per client.
builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.KeyedClientConfigurations = true;
});

var app = builder.Build();

var settingsApi = app.MapGroup("/settings");

// Base values + the clients declared by the overrides.
settingsApi.MapGet("/", (IConfiguration configuration) => new Settings[] {
    new (Key: PG_KEY, Value: MaskPassword(configuration[PG_KEY])),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"]),
    new (Key: "clients", Value: string.Join(", ", configuration.GetClientNames()))
});

// One client: its overrides laid over the base values. An unknown client reads the base.
settingsApi.MapGet("/{client}", (string client, IConfiguration configuration) =>
{
    IConfiguration view = configuration.ForClient(client);
    return new Settings[] {
        new (Key: "theme", Value: view["theme"]),
        new (Key: "language", Value: view["language"]),
        new (Key: "notifications", Value: view["notifications"]),
    };
});

// Refresh the dynamic layer only: files on disk are not re-read.
settingsApi.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
{
    await reloader.ReloadDynamicAsync();
    return Results.Ok();
});
```

### Expected responses

`GET /settings` — base values and the discovered clients:

```json
[
  { "key": "sa:pg:connection", "value": "Username=postgres;Password=***;Host=localhost;Port=5432;Database=postgres;Pooling=True;Search Path=public;Command Timeout=180" },
  { "key": "theme",             "value": "dark" },
  { "key": "language",          "value": "en" },
  { "key": "notifications",     "value": "enabled" },
  { "key": "secret",            "value": "ENCRYPTED DEMO SECRET" },
  { "key": "clients",           "value": "acme, beta" }
]
```

`GET /settings/acme` — the overlay (only the values acme overrides change):

```json
[
  { "key": "theme",             "value": "light" },
  { "key": "language",          "value": "en" },
  { "key": "notifications",     "value": "disabled" }
]
```

`GET /settings/beta` — `"language": "de"`, everything else from the base.

Note the password is masked: `NpgsqlConnectionStringBuilder` normalises the keywords it
round-trips, so the output reads `Username=…` / `Search Path=…` rather than the
`User ID=…` / `SearchPath=…` spelling used in `secrets.txt`. The value is a re-render of the
connection string, not the raw one.

---

## Dependencies

| Package | Purpose |
|---------|---------|
| `Sa.Configuration` | Layer builder, secrets management, per-client overlay, dynamic reload |
| `Sa.Configuration.PostgreSql` | Dynamic config from PostgreSQL, including per-client rows |
| `Sa.Data.PostgreSql` | Npgsql client wrapper |
| `Npgsql` | `NpgsqlConnectionStringBuilder` for masking the password; arrives transitively, but the sample uses it directly |
| `Microsoft.AspNetCore.OpenApi` | OpenAPI support (dev only) |

---

## License

MIT
