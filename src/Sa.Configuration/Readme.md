# Sa.Configuration

Secure secrets management and command-line argument parsing within the .NET `Microsoft.Extensions.Configuration` ecosystem. Secrets are automatically substituted into configuration without manual application code.

---

## Features

- **Automatic secret substitution**: `{{key}}` placeholders are replaced with real values from files, environment variables, or command-line arguments
- **Value normalization**: secret values are trimmed; surrounding quotes (`'`, `"`, `` ` ``) are stripped
- **Cycle protection**: nested placeholders resolve up to a depth of 3; circular references throw `InvalidOperationException`
- **Optional placeholders**: `{{?key}}` — if the secret is not found, the whole string becomes `null` instead of throwing
- **Chained Stores**: multiple secret sources with LIFO priority (the store added last wins); `AddStore` is thread-safe
- **Argument parser**: supports `--key value`, `--key=value`, `-flag` formats; negative numbers are treated as values; surrounding quotes are stripped
- **Environments**: automatic loading of `secrets.{Environment}.txt` (Development/Staging/Production)

---

## Quick Start

### 1. Register in `Program.cs`

```csharp
using Sa.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Connects arguments + secrets from files/env vars/command line
builder.Configuration.AddSaConfiguration();

var app = builder.Build();
```

### 2. Secrets File (`secrets.txt`)

```ini
# Postgres
sa_pg_host=localhost
sa_pg_user=postgres
sa_pg_port=5432
sa_pg_database=myapp
sa_pg_schema=public
sa_pg_password=superSecret123

# API keys
api_key=abc123xyz
jwt_secret=h8k2m9p0
```

> ⚠️ Add `secrets*.txt` to `.gitignore`!

### 3. Placeholders in `appsettings.json`

```json
{
  "secret": "{{sa_secret}}",

  "sa": {
    "pg": {
      "connection": "User ID={{sa_pg_user}};Password={{sa_pg_password}};Host={{sa_pg_host}};Port={{sa_pg_port}};Database={{sa_pg_database}};Pooling=true;SearchPath={{sa_pg_schema}};Command Timeout=180;"
    }
  },

  "ExternalApi": {
    "ApiKey": "{{api_key}}"
  }
}
```

### 4. Reading Configuration

```csharp
var pgConn = app.Configuration["sa:pg:connection"];
// → "User ID=postgres;Password=superSecret123;Host=localhost;..."
```

---

## Secret Priority Order

Secrets use LIFO priority — the store added last wins. The default chain is resolved in this order:

| # | Source | Example |
|---|--------|---------|
| 1 | Command-line arguments | `--sa_pg_password=...` |
| 2 | Environment variables | `SA_PG_PASSWORD=...` |
| 3 | Environment-specific file | `secrets.Development.txt` |
| 4 | Base secrets file | `secrets.txt` |

The first source with a value wins — command line can override everything, environment variables override files, and so on.

---

## Optional Placeholders

Use `{{?key}}` instead of `{{key}}` to avoid an error when a secret is missing:

```json
{
  "optional_feature": "{{?feature_flag}}"
}
```

If `feature_flag` is not found in any store, the whole string is `null`.

In the configuration pipeline (`AddSaConfiguration` / `AddSaPostSecretProcessing`), any missing secret — including a plain `{{key}}` — results in `null` for that value instead of throwing.

---

## Usage with Sa.Configuration.PostgreSql

```csharp
using Sa.Configuration;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// First, standard sources (appsettings.json, secrets.txt)
builder.Configuration.AddSaConfiguration();

// Then, dynamic settings from the database
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM app_settings"
));

var app = builder.Build();
```

---

## Arguments

```csharp
using Sa.Configuration.CommandLine;

// some.exe --config_db /share/data.db --debug --port -5
var args = new Arguments(args);

string? configDb = args["config_db"];       // → "/share/data.db"
bool?   debug    = args.GetBool("debug");   // → true
int?    port     = args.GetInt("port");     // → -5
TimeSpan? timeout = args.GetTimeSpan("timeout");
```

Supported formats:

```
--key value
--key=value
-key value
-key=value
-flag            → flag=true (boolean flag)
--key -5         → value "-5" (negative numbers are never treated as flags)
```

Typed getters return `null` when the parameter is absent or invalid:

| Method | Return Type | Conversion |
|--------|------------|------------|
| `GetBool()` | `bool?` | `"true"/"1"/"yes"/"on"` → `true` |
| `GetInt()` | `int?` | `int.TryParse(..., InvariantCulture)` |
| `GetFloat()` | `float?` | same as above |
| `GetLong()` | `long?` | same as above |
| `GetTimeSpan()` | `TimeSpan?` | `TimeSpan.TryParse(..., InvariantCulture)` |

`args.Contains("key")` checks for parameter existence; `args.Parameters` gives access to the full parsed dictionary.

---

## Secrets

Use `Secrets` directly when you need secrets outside of `IConfiguration`:

```csharp
using Sa.Configuration.SecretStore;

// Default chain (LIFO): CLI args → env vars → secrets.{Env}.txt → secrets.txt
var secrets = Secrets.CreateDefault();

string? result   = secrets.PopulateSecrets("Server={{host}};Pwd={{sa_pg_password}}");
string? password = secrets.GetSecret("sa_pg_password");
```

Custom chain — any `ISecretStore` (`FileSecretStore`, `EnvironmentVariableSecretStore`, `CommandLineArgsSecretStore`, `InMemorySecretStore`):

```csharp
var secrets = new Secrets(new FileSecretStore("my-secrets.txt"));
secrets.AddStore(new InMemorySecretStore().AddSecret("override_key", "override_value"));
// LIFO: the newly added store has the highest priority
```

`Secrets.CreateDefault(new SecretOptions { FileName = "app-secrets.txt", EnvironmentName = "Staging" })` customizes the base file name and environment. `Secrets.GetEnvironmentName()` resolves it from `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` / `environment`, falling back to `Production`.

---

## License

MIT
