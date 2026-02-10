# Working with Secrets via Configuration

The `Sa.Configuration` library provides **secure and transparent integration of secrets** into the standard .NET configuration system. All sensitive data is automatically substituted during configuration loading — without manual processing in application code.

---

## How It Works

1. **Load secrets** from secure sources (files, environment variables)
2. **Automatic substitution** of values in configuration during loading
3. **Transparent usage** via standard `IConfiguration`

---

## Setup

### 1. Registration in `Program.cs`

```csharp
using Sa.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddSaConfiguration();

var app = builder.Build();
```

### 2. Secret Sources

Secrets are loaded from the following sources (in priority order):

| Source | Description | Example |
|--------|-------------|---------|
| **Secrets file** | Text file with `key=value` pairs | `secrets.txt` |
| **Environment variables** | System environment variables | `SA_PG_PASSWORD=myPass` |
| **Command-line arguments** | Application startup parameters | `--sa_pg_port=5432` |

---

## Secrets File Format (`secrets.txt`)

```ini
# Postgres
sa_pg_host=localhost
sa_pg_user=postgres
sa_pg_port=5432
sa_pg_database=postgres
sa_pg_schema=public
sa_pg_password=superSecret123

# Other secrets
sa_secret=TOP SECRET!
api_key=abc123xyz
```

> ⚠️ **Important**: The `secrets.txt` file must be excluded from version control (.gitignore)

---

## Usage in `appsettings.json`

Specify **placeholders** in the format `{{secret_key}}`:

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

---

## Code Example

### Retrieving values via `IConfiguration`

```csharp
var todosApi = app.MapGroup("/settings");

todosApi.MapGet("/", (IConfiguration configuration) => new Settings[] {
    new (Key: "pg_connection", Value: configuration["sa:pg:connection"]),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "secret", Value: configuration["secret"])
}).WithName("GetSettings");
```


---

## What Happens Under the Hood

```
┌─────────────────────────────────────────────────────────┐
│  1. appsettings.json contains:                          │
│     "connection": "Host={{sa_pg_host}};Password={{...}}" │
├─────────────────────────────────────────────────────────┤
│  2. secrets.txt contains:                               │
│     sa_pg_host=localhost                                │
│     sa_pg_password=superSecret123                       │
├─────────────────────────────────────────────────────────┤
│  3. IConfiguration["sa:pg:connection"] returns:         │
│     "Host=localhost;Password=superSecret123;..."        │
└─────────────────────────────────────────────────────────┘
```

---

## Advantages

- **Security**: secrets are not stored in code or configuration files
- **Flexibility**: supports multiple secret sources
- **Simplicity**: transparent operation through standard `IConfiguration`
- **Debugging**: easy to switch secrets via environment variables or arguments

---

## Tips

- For local development, create `secrets.Development.txt`; for production — use environment variables
- Never commit secret files to the repository
- Use different secret files for different environments (dev, staging, prod)

---

# Core Classes

## Arguments Class

The `Arguments` class is designed to parse command-line arguments passed to a C# application. It provides a dictionary-like interface for easy parameter retrieval and supports both single-value and multi-value parameters.

**Key Features:**
- **Parameter Parsing**: Splits command-line arguments into key-value pairs
- **Easy Retrieval**: Access parameter values using an indexer
- **Default Handling**: Automatically assigns default values for boolean flags

**Example:**
```csharp
// some.exe --config_db /share/data.db
var arguments = new Arguments(args);
string? configDb = arguments["config_db"];
```

---

## Secrets Class

The `Secrets` class provides a secure mechanism for managing sensitive information such as API keys and database passwords from various sources. It supports loading secrets from files, environment variables, and dynamically generated host key files.

**Key Features:**
- **Chained Secret Stores**: Combines multiple sources for retrieving secrets
- **Dynamic Loading**: Supports environment-specific configurations
- **Placeholder Replacement**: Easily populates strings with secret values

**Example:**
```csharp
string input = "Database password: {{db_password}}";
string? populatedString = service.PopulateSecrets(input);
```
