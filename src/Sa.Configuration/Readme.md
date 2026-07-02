# Sa.Configuration

Secure secrets management and command-line argument parsing within the .NET `Microsoft.Extensions.Configuration` ecosystem. Secrets are automatically substituted into configuration without manual application code.

---

## Features

- **Automatic secret substitution**: `{{key}}` placeholders are replaced with real values from files, environment variables, or command-line arguments
- **Cycle protection**: built-in guard against infinite recursion during placeholder resolution
- **Optional placeholders**: `{{?key}}` — if the secret is not found, returns `null` instead of throwing
- **Chained Stores**: multiple secret sources with priority ordering
- **Argument parser**: supports `--key value`, `--key=value`, `-flag` formats
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

Secrets are looked up in descending priority order:

| # | Source | Example File |
|---|--------|-------------|
| 1 | Base secrets file | `secrets.txt` |
| 2 | Environment-specific file | `secrets.Development.txt` |
| 3 | Environment variables | `SA_PG_PASSWORD=...` |
| 4 | Command-line arguments | `--sa_pg_password=...` |

The first source that has a value wins. This allows overriding secrets per environment.

---

## Optional Placeholders

Use `{{?key}}` instead of `{{key}}` to avoid an error when a secret is missing:

```json
{
  "optional_feature": "{{?feature_flag}}"
}
```

If `feature_flag` is not found in any store, `null` is returned.

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

## Arguments — Command-Line Argument Parser

```csharp
using Sa.Configuration.CommandLine;

// some.exe --config_db /share/data.db --debug
var args = new Arguments(args);

string? configDb = args["config_db"];       // → "/share/data.db"
bool?   debug    = args.GetBool("debug");   // → true
int?    port     = args.GetInt("port");     // → null
TimeSpan? timeout = args.GetTimeSpan("timeout");
```

Supported formats:

```
--key value
--key=value
-key value
-key=value
-flag          → flag=true (boolean flag)
```

Typed methods return `null` when the parameter is absent or invalid:

| Method | Return Type | Conversion |
|--------|------------|------------|
| `GetBool()` | `bool?` | `"true"/"1"/"yes"/"on"` → `true` |
| `GetInt()` | `int?` | `int.TryParse(..., InvariantCulture)` |
| `GetFloat()` | `float?` | same as above |
| `GetLong()` | `long?` | same as above |
| `GetTimeSpan()` | `TimeSpan?` | `TimeSpan.TryParse(..., InvariantCulture)` |

Additional methods:

| Method | Return Type | Description |
|--------|------------|-------------|
| `Contains(param)` | `bool` | Checks if parameter exists |
| `IsPresent(param)` | `bool` | Parameter exists AND has a non-null value |

---

## Secrets — Secrets Management

### Creating Defaults

```csharp
using Sa.Configuration.SecretStore;

// Standard chain: File → File.Env → EnvVar → CommandLine
var secrets = Secrets.CreateDefault();
```

### Custom Chain

```csharp
var secrets = new Secrets(
    new FileSecretStore("my-secrets.txt"),
    new EnvironmentVariableSecretStore(),
    new InMemorySecretStore(new Dictionary<string, string?> {
        { "override_key", "override_value" }
    })
);
```

### Fluent Addition at Runtime

```csharp
secrets.AddStore(new FileSecretStore("additional-secrets.txt"));
```

### Placeholder Substitution

```csharp
string template = "Server={{host}};Password={{password}}";
string result = secrets.PopulateSecrets(template);
// → "Server=localhost;Password=s3cret!"
```

### Getting a Single Secret

```csharp
string? password = secrets.GetSecret("sa_pg_password");
```

### Environment Name Resolution

```csharp
string env = Secrets.GetEnvironmentName();
// → "Development", "Staging", "Production", etc.
```

---

## Public API

### Namespace `Sa.Configuration`

| Type | Purpose |
|------|---------|
| `Setup.AddSaConfiguration()` | Main entry-point: connects arguments + secret processing |

### Namespace `Sa.Configuration.CommandLine`

| Type | Purpose |
|------|---------|
| `Arguments` | Command-line argument parser |
| `Arguments.CreateDefault()` | Creates from `Environment.GetCommandLineArgs()` |
| `Setup.AddSaCommandLine()` | Extension method for `IConfigurationBuilder` |

### Namespace `Sa.Configuration.SecretStore`

| Type | Purpose |
|------|---------|
| `Secrets` | Main secrets management class, implements `ISecretService` |
| `Secrets.CreateDefault()` | Standard store chain |
| `Secrets.GetEnvironmentName()` | Resolves environment (`DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT`) |
| `SecretOptions` | Options for `CreateDefault()`: `FileName`, `Args`, `EnvironmentName` |
| `ISecretService` | Interface: `PopulateSecrets()` + `GetSecret()` |
| `ISecretStore` | Interface: `GetSecret(string key)` |
| `Setup.AddSaPostSecretProcessing()` | Extension method: applies `ISecretService` to config AFTER other sources are loaded |

### Secret Stores (`Sa.Configuration.SecretStore.Stories`)

| Class | Description |
|-------|-------------|
| `FileSecretStore` | Loads `key=value` from a text file (skips `#` comments) |
| `EnvironmentVariableSecretStore` | Reads from `Environment.GetEnvironmentVariable()` |
| `CommandLineArgsSecretStore` | Pulls secrets from `Arguments` |
| `InMemorySecretStore` | Dictionary in memory, fluent `.AddSecret()` |

---

## How It Works

```
┌──────────────────────────────────────────────────────┐
│ 1. appsettings.json contains:                        │
│    "connection": "Host={{sa_pg_host}};Password={{...}}"│
├──────────────────────────────────────────────────────┤
│ 2. secrets.txt contains:                             │
│    sa_pg_host=localhost                              │
│    sa_pg_password=s3cret!                            │
├──────────────────────────────────────────────────────┤
│ 3. AddSaPostSecretProcessing substitutes placeholders:│
│    IConfiguration["sa:pg:connection"]                │
│    → "Host=localhost;Password=s3cret!;..."           │
└──────────────────────────────────────────────────────┘
```

---

## License

MIT
