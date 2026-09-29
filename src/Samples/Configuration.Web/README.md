# Configuration.Web

ASP.NET Core Minimal API sample demonstrating **Sa.Configuration** — CLI argument parsing, secure secrets management from files/environment variables with `{{placeholder}}` substitution, and dynamic PostgreSQL-backed configuration.

---

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

1. **Secrets Management** — `{{sa_secret}}` placeholders in `appsettings.json` are resolved from a chain of stores: environment variables → CLI arguments → `secrets.txt` file.
2. **Dynamic Configuration from PostgreSQL** — Application settings (`theme`, `language`, `notifications`) are stored in a DB table and reflected in-app without restart.
3. **CLI Argument Parsing** — The `Arguments` class is wired through `AddSaConfiguration()` for command-line secret overrides.
4. **Zero ORM** — No EF Core or migrations. Just raw Npgsql with `CREATE TABLE IF NOT EXISTS`.

---

## Architecture

```
AddSaConfiguration()                         [Sa.Configuration]
  ├── AddSaCommandLine(args)         → CommandLineArgsSecretStore
  └── AddSaPostSecretProcessing()    → ChainedSecrets(
           │                               │     ├── EnvironmentVariableSecretStore
           │                               │     ├── CommandLineArgsSecretStore
           │                               │     └── FileSecretStore (secrets.txt)
           │                              )

AddSaPostgreSqlConfiguration(options)         [Sa.Configuration.PostgreSql]
  └── DatabaseConfigurationProvider   → Dynamic settings from PostgreSQL
```

`AddSaConfiguration()` and `AddSaPostgreSqlConfiguration` are independent calls from separate packages: `Sa.Configuration` does not reference `Sa.Configuration.PostgreSql`, so the PostgreSQL source is wired in with its own line rather than being nested in the secret chain.

---

## Configuration Chain

The placeholder format `{{key}}` resolves values from the chained secret stores in order:

1. **Environment Variables** — e.g., `SA_SECRET="My Secret"`
2. **CLI Arguments** — e.g., `/sa_secret:"Override from CLI"`
3. **secrets.txt** — Plain text file with `key=value` pairs (comments with `#`, auto-trimmed quotes)

Optional variant `{{?key}}` returns `null` instead of throwing if the key is missing.

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

// Step 1: Wire up secrets + CLI args + env vars
builder.Configuration.AddSaConfiguration();

const string PG_KEY = "sa:pg:connection";
string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

// Step 2: Create PostgreSQL data source
using var ds = IPgDataSource.Create(connectionString);

// Step 3: Create settings table (if not exists)
ds.ExecuteScalar("""
    CREATE TABLE IF NOT EXISTS settings (
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );
    INSERT INTO settings (key, value)
    VALUES ('theme', 'dark'), ('language', 'en'), ('notifications', 'enabled')
    ON CONFLICT (key) DO NOTHING;
""", null).Wait();

// Step 4: Add PostgreSQL as dynamic config source
// The application already holds a data source, so hand it over instead of letting the
// configuration source open a second pool. Ownership stays here — `ds` is never disposed
// by the provider, and it carries the connection, so the connection string is ignored.
builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions(
        ConnectionString: string.Empty,
        // Name the columns: the provider takes the first two as (key, value), so the column
        // order is part of the contract and `select *` would make it an implicit one.
        SelectSql: "select key, value from settings"),
    ds);

// Step 5: Register endpoints
var app = builder.Build();

var todosApi = app.MapGroup("/settings");

// A connection string carries the password, so never hand one to a client as-is.
// Mask it for display; everything else on this endpoint is shown verbatim.
static string MaskPassword(string? connectionString) => connectionString is null
    ? string.Empty
    : new NpgsqlConnectionStringBuilder(connectionString) { Password = "***" }.ConnectionString;

todosApi.MapGet("/", (IConfiguration configuration) => new Settings[] {
    new (Key: PG_KEY, Value: MaskPassword(configuration[PG_KEY])),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"])
}).WithName("GetSettings");
```

### Expected Response

```json
[
  { "key": "sa:pg:connection", "value": "Username=postgres;Password=***;Host=localhost;Port=5432;Database=postgres;Pooling=True;Search Path=public;Command Timeout=180" },
  { "key": "theme",             "value": "dark" },
  { "key": "language",          "value": "en" },
  { "key": "notifications",     "value": "enabled" },
  { "key": "secret",            "value": "TOP SECRET!" }
]
```

Note the password is masked: `NpgsqlConnectionStringBuilder` normalises the keywords it
round-trips, so the output reads `Username=…` / `Search Path=…` rather than the
`User ID=…` / `SearchPath=…` spelling used in `secrets.txt`. The value is a re-render of the
connection string, not the raw one.

---

## Dependencies

| Package | Purpose |
|---------|---------|
| `Sa.Configuration` | Secrets management, CLI argument parsing |
| `Sa.Configuration.PostgreSql` | Dynamic config from PostgreSQL |
| `Sa.Data.PostgreSql` | Npgsql client wrapper |
| `Npgsql` | `NpgsqlConnectionStringBuilder` for masking the password; arrives transitively, but the sample uses it directly |
| `Microsoft.AspNetCore.OpenApi` | OpenAPI support (dev only) |

---

## License

MIT
