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
3. **Per-client configuration** — the shared values live in `setting`, each client's overrides live in `client_settings` split by `client_id`; `GET /settings/{client}` lays them over the core values, and no background service is registered. A known client can also be injected by key (`GET /settings/acme`).
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

// Static layer: the host already added appsettings/env/args. Sa adds the secret pass.
builder.Configuration.AddSaConfiguration(sa => sa.UseSecrets());

const string PG_KEY = "sa:pg:connection";
string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

using var ds = IPgDataSource.Create(connectionString);

// Two tables: `setting` holds the shared core values, `client_settings` holds the clients'
// overrides split by client_id. The key names are the same in both tables.
ds.ExecuteScalar("""
    CREATE TABLE IF NOT EXISTS setting (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );

    INSERT INTO setting (key, value) VALUES
        ('s3_url',        'A'),
        ('theme',         'dark'),
        ('language',      'en'),
        ('notifications', 'enabled')
    ON CONFLICT DO NOTHING;

    CREATE TABLE IF NOT EXISTS client_settings (
        client_id TEXT NOT NULL,
        key       TEXT NOT NULL,
        value     TEXT NOT NULL,
        PRIMARY KEY (client_id, key)
    );

    INSERT INTO client_settings (client_id, key, value) VALUES
        ('acme', 's3_url',        'B1'),
        ('acme', 'theme',         'light'),
        ('acme', 'notifications', 'disabled'),
        ('beta', 's3_url',        'C'),
        ('beta', 'language',      'de')
    ON CONFLICT DO NOTHING;
""", null).Wait();

// Dynamic layer: the core values come from `setting`, through the application's own pool.
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.DataSource(ds),
    new PostgreSqlConfigurationOptions
    {
        SelectSql = "SELECT key, value FROM setting",
    });

// Client layer: one query returns every client's rows, the registry groups them by client_id and
// builds a root per client. Keys are stored verbatim — no prefix.
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IPgDataSource>(static (dataSource, cancellationToken) =>
        ReadClientSettingsAsync(dataSource, cancellationToken));
});

builder.Services.AddSingleton(ds);

// A known client can be injected by key: the service constructor receives acme's own root.
builder.Services.AddSingleton<AcmeStorage>();

// Runtime: reload on demand (no background timer unless DynamicReload is switched on). One
// ReloadDynamicAsync refreshes the core and the client roots.
builder.Services.AddSaConfigurationRuntime(builder.Configuration);

var app = builder.Build();

var settingsApi = app.MapGroup("/settings");

// Base (core) values and the clients reported by the provider.
settingsApi.MapGet("/", (IConfiguration configuration, IClientConfigurations clients) => new Settings[] {
    new (Key: PG_KEY, Value: MaskPassword(configuration[PG_KEY])),
    new (Key: "s3_url", Value: configuration["s3_url"]),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"]),
    new (Key: "clients", Value: string.Join(", ", clients.Clients))
});

// One client: its own root laid over the core values. An unknown client reads the core only.
settingsApi.MapGet("/{client}", (string client, IClientConfigurations clients) =>
{
    IConfiguration clientConfiguration = clients.GetClient(client);

    return new Settings[] {
        new (Key: "s3_url", Value: clientConfiguration["s3_url"]),
        new (Key: "theme", Value: clientConfiguration["theme"]),
        new (Key: "language", Value: clientConfiguration["language"]),
        new (Key: "notifications", Value: clientConfiguration["notifications"]),
        new (Key: "secret", Value: clientConfiguration["secret"]),
    };
});

// Keyed injection for a known client: the client's root arrives through the constructor, so the
// handler never touches the registry. `/settings/acme` is a literal route, so it wins over
// `/settings/{client}`.
settingsApi.MapGet("/acme", (AcmeStorage acme) => new Settings[] {
    new (Key: "s3_url", Value: acme.S3Url),
    new (Key: "theme", Value: acme.Theme),
    new (Key: "language", Value: acme.Language),
    new (Key: "notifications", Value: acme.Notifications),
});

// Refresh the dynamic layer only: files on disk are not re-read. The client providers are re-read
// in the same call.
settingsApi.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
{
    await reloader.ReloadDynamicAsync();
    return Results.Ok();
});

// The client provider: flat (client_id, key, value) rows read by one query.
static async ValueTask<IReadOnlyList<ClientSetting>> ReadClientSettingsAsync(
    IPgDataSource dataSource,
    CancellationToken cancellationToken)
{
    List<ClientSetting> rows = [];

    await dataSource.ExecuteReaderAsync(
        "SELECT client_id, key, value FROM client_settings",
        (reader, _) => rows.Add(new ClientSetting(
            reader.GetString(0), reader.GetString(1), reader.GetString(2))),
        initCommand: null,
        cancellationToken);

    return rows;
}

public static class DemoClients
{
    public const string Acme = "acme";
}

// A known client resolved from the container by key: the constructor receives acme's own root,
// already laid over the core values.
public sealed class AcmeStorage([FromKeyedServices(DemoClients.Acme)] IConfiguration configuration)
{
    public string? S3Url => configuration["s3_url"];
    public string? Theme => configuration["theme"];
    public string? Language => configuration["language"];
    public string? Notifications => configuration["notifications"];
}
```

### Expected responses

`GET /settings` — core values and the clients reported by the provider:

```json
[
  { "key": "sa:pg:connection", "value": "Username=postgres;Password=***;Host=localhost;Port=5432;Database=postgres;Pooling=True;Search Path=public;Command Timeout=180" },
  { "key": "s3_url",           "value": "A" },
  { "key": "theme",            "value": "dark" },
  { "key": "language",         "value": "en" },
  { "key": "notifications",    "value": "enabled" },
  { "key": "secret",           "value": "ENCRYPTED DEMO SECRET" },
  { "key": "clients",          "value": "acme, beta" }
]
```

`GET /settings/acme` — the literal route: acme's root injected into `AcmeStorage` by key. Only the
rows that exist change, and this route exposes just the four fields the service reads:

```json
[
  { "key": "s3_url",        "value": "B1" },
  { "key": "theme",         "value": "light" },
  { "key": "language",      "value": "en" },
  { "key": "notifications", "value": "disabled" }
]
```

`GET /settings/beta` — the `{client}` route: `s3_url` = `C`, `language` = `de`, everything else from
the core.

Note the password is masked: `NpgsqlConnectionStringBuilder` normalises the keywords it
round-trips, so the output reads `Username=…` / `Search Path=…` rather than the
`User ID=…` / `SearchPath=…` spelling used in `secrets.txt`. The value is a re-render of the
connection string, not the raw one.

---

## Dependencies

| Package | Purpose |
|---------|---------|
| `Sa.Configuration` | Layer builder, secrets management, per-client configuration, dynamic reload |
| `Sa.Configuration.PostgreSql` | Dynamic config from PostgreSQL, including per-client rows |
| `Sa.Data.PostgreSql` | Npgsql client wrapper |
| `Npgsql` | `NpgsqlConnectionStringBuilder` for masking the password; arrives transitively, but the sample uses it directly |
| `Microsoft.AspNetCore.OpenApi` | OpenAPI support (dev only) |

---

## License

MIT
