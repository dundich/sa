# Sa.Configuration

A .NET package that does two things:

1. **Substitutes secrets into configuration** — instead of hardcoding passwords and keys, you write `{{sa_pg_password}}` in the config, and the package fills in the real value from a file, an environment variable, or a command-line argument.
2. **Parses command-line arguments** — `--key value`, `--key=value`, `-flag`, and so on.

It plugs into the standard `Microsoft.Extensions.Configuration` pipeline, so it slots right into a normal ASP.NET setup.

---

## What it can do

- **Automatic secret substitution** — `{{key}}` placeholders are replaced with real values. See [Quick start](#quick-start).
- **Value cleanup** — values are trimmed at the edges and stripped of surrounding quotes (`'`, `"`, `` ` ``). Details in [Encodings](#encodings).
- **Cycle protection** — nested placeholders are expanded up to 3 levels deep. If a cycle forms, you get a clear `InvalidOperationException`. Details in [Secrets](#secrets).
- **Optional placeholders** — `{{?key}}` instead of `{{key}}`: if the secret is missing, the string becomes `null` instead of failing with an error.
- **Multiple stores at once** — [`Secrets`](#secrets) can combine any number of stores (`ISecretStore`). The one added last wins. Stores can be added at runtime, including from multiple threads.
- **Argument parsing** — [`Arguments`](#arguments) understands `--key value`, `--key=value`, `-flag`. Negative numbers are treated as values, not flags. Quotes are stripped. `__` in a parameter name is read as the `:` separator.
- **Environments** — automatically picks up `secrets.{Environment}.txt` for Development/Staging/Production.
- **Key aliases** — [Key aliases](#key-aliases): you can link a "flat" setting name (say, `auth_base_url` from the PHP world) to a nested C# path (`sa:scope:api:auth:base_url`). Works across all sources at once.
- **Encryption** — encrypted `secrets_enc*.txt` files (AES-256-GCM) can be committed to the repo. Encryption keys are plain or `base64:`. Clients, environments, `.env`, and Unicode are supported. See [Encrypted secrets](#encrypted-secrets).

---

## Quick start

### 1. Wire up the package in `Program.cs`

```csharp
using Sa.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Hook up arguments + secrets from files/ENV/CLI
builder.Configuration.AddSaConfiguration();

var app = builder.Build();
```

### 2. Create a secrets file `secrets.txt`

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

> ⚠️ Don't forget to add `secrets*.txt` to `.gitignore`!

### 3. Write placeholders in `appsettings.json`

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

### 4. Read the configuration as usual

```csharp
var pgConn = app.Configuration["sa:pg:connection"];
// → "User ID=postgres;Password=superSecret123;Host=localhost;..."
```

---

## Where secrets come from (priority order)

Sources are checked in order. The first one that has the value wins. Higher in the list means more important:

| # | Source | Example |
|---|--------|---------|
| 1 | Command-line arguments | `--sa_pg_password=...` |
| 2 | Environment variables | `SA_PG_PASSWORD=...` |
| 3 | `.env.{env}` / `.env` | `SA_SECRETS_DECRYPTION_KEY=...` |
| 4 | `secrets_enc.{client}.{env}.txt` / `secrets_enc.{client}.txt` | encrypted secrets committed to the repo |
| 5 | `secrets_enc.{env}.txt` / `secrets_enc.txt` | encrypted default values |
| 6 | `secrets.{env}.txt` | regular environment-specific file |
| 7 | `secrets.txt` | regular base file |

Put simply: the command line overrides everything, environment variables override files, and environment-specific files override the base file.

What `{env}` means: the environment name taken from `DOTNET_ENVIRONMENT` → `ASPNETCORE_ENVIRONMENT` → `environment`. If none is set — `Production`. Want a short name like `dev`? Set `DOTNET_ENVIRONMENT=dev`, and all file names will follow it.

---

## Encrypted secrets

Encrypted files can be safely committed to the repo: values are encrypted with AES-256-GCM, while key names and comments stay readable — convenient for diffs.

How to use:

1. Create `secrets_enc*.txt` files.
2. Set the decryption key:

```bash
export SA_SECRETS_DECRYPTION_CLIENT=lsr
export SA_SECRETS_DECRYPTION_KEY=...        # or base64:<32-byte key>
# the same variables can go into .env (the .env itself is not committed)
```

Possible file names:

- `secrets_enc.txt`
- `secrets_enc.{env}.txt`
- `secrets_enc.{client}.txt`
- `secrets_enc.{client}.{env}.txt`

`{env}` is the exact environment name (`Development`, `Production`, or whatever is in `DOTNET_ENVIRONMENT`).

### Example encrypted file

Here's the same `secrets.txt` from the [quick start](#2-create-a-secrets-file-secretstxt), but encrypted for client `lsr` and environment `dev`:

```
# saenc: v1
# saenc: kdf=pbkdf2-sha256
# saenc: iterations=600000
# saenc: salt=dFgi+38h+4oDUbhsLEUaXg==
# saenc: client=lsr
# saenc: env=dev
# Postgres
sa_pg_host=ENC1:RkVQFBmvvoUBMLkGSXznSaS9mW0h9MVPltZIz4Oou9Q/PEjWiw==
sa_pg_user=ENC1:pBA9GEfkNcZ8+OxP2KWlsfgzDigAq/NWopcYW4GKH4+Qo5Zr
sa_pg_password=ENC1:8zPuIVo2/qAa9OO9mafijG1OYuq1Ku6i7Hg8QRoG2Q8n25dtvb0P7Z1x

# API keys
api_key=ENC1:Nzz7pPfq4K6rrAixJWQhN37Inu/n6jL+rkFGpJbKyfqAuOs4Aw==
jwt_secret=ENC1:MHP6RX5G7Xc5v+QaSkdhaxVRDRQg8NfCr4OjJzw0BhNEFFgv
```

What stays readable: keys, comments, header. What gets encrypted: only the values.

This block was actually generated by `do-secrets.sh` with the one-time passphrase `demo-passphrase-not-a-real-secret` — so `decrypt` and `check` can be tested directly on it.

The same approach with a "raw" key (no `iterations` / `salt`, `kdf=none`, key `SA_SECRETS_DECRYPTION_KEY=base64:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=`):

```
# saenc: v1
# saenc: kdf=none
# saenc: client=lsr
# saenc: env=prod
sa_pg_password=ENC1:fFodkm/LnlPzC6wIu32wkVAvCbXx6th6qG/So7DsX7MSmOUPpRumfOxb
```

Scripts for encryption and verification live in the repo:

```bash
./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets_enc.lsr.dev.txt
./build-sh/do-secrets.sh decrypt secrets_enc.lsr.dev.txt
./build-sh/do-secrets.sh check
```

Important behavior: if an encrypted file exists but the key isn't found, the application **fails at startup**. This is intentional — so it never silently falls back to plaintext.

Format, per-client keys, and error handling are described in `SecretStore/Readme.md`.

### Encodings

Everything runs in UTF-8. The only "non-UTF-8" token in a file is the Base64 payload (ciphertext).

| What | How it works |
|------|--------------|
| File encoding | UTF-8. BOM is allowed and ignored. Files without BOM are read as UTF-8 — if yours are CP1251/CP866, re-save them as UTF-8. |
| Line endings | LF, CRLF, and CR are read correctly. When writing (`SecretsCipher` / `do-secrets.sh`), LF is always used, so re-encryption doesn't break diffs. |
| How a value becomes ciphertext | UTF-8 bytes → AES-256-GCM → `nonce(12) ‖ tag(16) ‖ ciphertext` → standard Base64 on one line. |
| Value prefix | `ENC1:` — format version 1 marker. It isn't authenticated itself; the key name is authenticated, so the blob can't be moved to another key. |
| Payload length | `4 × ceil((28 + UTF-8 byte count) / 3)`. Examples: `localhost` (9 bytes) → 52 chars, `superSecret123` (14 bytes) → 56, `Привет` (12 bytes) → 56. |
| Unicode | Keys and values round-trip as UTF-8: `Привет`, `pä£s=word`, emoji. Non-ASCII takes more bytes — hence the longer blob. |
| `=` inside values | Allowed. The line is split on the first `=`, so `api_key=abc=123` is preserved intact. |
| Empty values | Encrypted and restored empty (`empty=`), no error. |
| Whitespace | Keys and values are trimmed at the edges (` key = value ` → `key=value`). Internal spaces are kept. |
| Comments | A line that starts with `#` after trim is copied verbatim. A `# saenc:` comment in the source file is rejected as "already encrypted". |
| Header metadata | `client` / `env` are trimmed, must not contain line breaks, and are checked against the file name on load. |
| Passphrase | Any UTF-8 text, trimmed. Spaces and non-ASCII are allowed. |
| "Raw" key | `base64:` + Base64 exactly 32 bytes (44 chars with padding). Any other length → `32 bytes` error. |
| Salt | Base64 16 random bytes (24 chars) in the `# saenc: salt=` line. Length is checked, so editing the salt by hand breaks decryption. |
| Case | Key names, `ENC1:`, `base64:`, and header fields are case-sensitive. `# saenc: kdf=PBKDF2-SHA256` is not accepted. |
| Placeholder names | `{{key}}` matches `\w+` (Unicode letters, digits, underscore). Hyphens, dots, and spaces don't match — `{{api-key}}` stays as-is. |
| What gets substituted | The value is trimmed and stripped of quotes `'`, `"`, `` ` `` before being inserted into the configuration string. |

`.env` (which stores the decryption key) has its own rules: `KEY=VALUE`, `export KEY=VALUE`, `#` comments, optional UTF-8 BOM, CRLF/LF, quotes `'`, `"`, `` ` ``, and escaping of `\n`, `\r`, `\t`, `\"`, `\\` inside double quotes. Unknown escape sequences such as the Windows path `"C:\Users"` keep the backslash.

---

## Optional placeholders

Normally, if a secret isn't found, that's an error. But sometimes a setting is needed "if present". That's what `{{?key}}` is for:

```json
{
  "optional_feature": "{{?feature_flag}}"
}
```

If `feature_flag` isn't found in any store — the whole string becomes `null`. No exceptions.

By the way, in the configuration chain (`AddSaConfiguration` / `AddSaPostSecretProcessing`), **any** missing secret — even a regular `{{key}}` — yields `null` instead of an exception. So behavior there is softer than calling `PopulateSecrets` directly.

---

## Key aliases

### The problem

The same setting may have different names in different systems:

- In PHP (or in a database written by PHP) — a flat key: `auth_base_url`.
- In C# it's more convenient as a nested path: `sa:scope:api:auth:base_url`.

Rewriting the config or the code just for this is annoying. An alias links the two names: "when someone asks for the path — look at the flat key".

### How to set it

```csharp
using Sa.Configuration;

builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases =
        {
            ["sa:scope:api:auth:base_url"] = "auth_base_url",   // path → flat key
            ["sa:scope:api:auth:token_ttl"] = "auth_token_ttl",
        },
    },
});
```

On the left — the path C# reads. On the right — the name under which the value actually lives.

### What happens after that

Both names return the same value:

```csharp
app.Configuration["sa:scope:api:auth:base_url"]; // → "https://php.example.com"
app.Configuration["auth_base_url"];               // → "https://php.example.com"
```

The flat key doesn't go anywhere — it stays accessible. PHP code keeps working as before, and C# code gets its nested path.

### Where the value is looked up

The alias applies to the **final** configuration — the one already assembled from **all** sources (files, ENV, PostgreSQL, etc.). So the flat key and the canonical path can live **in different sources**:

- `auth_base_url` — in the PostgreSQL source.
- `sa:scope:api:auth:base_url` — in `appsettings.json`.

The alias will glue them together, and both paths will return the same value.

### Who overrides whom

- **By default, the alias wins.** A non-empty value from the flat key is written into the canonical path.
- **With `CanonicalValueWins = true`** — the other way round: the canonical value has priority. The alias fills the path only if it's empty.

```csharp
builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        CanonicalValueWins = true,   // canonical value wins
    },
});
```

- **If the alias is empty or missing** — the canonical path is not touched. It keeps its value (if any) or stays absent, and then a lower-priority source is responsible for it. No fake values are inserted.

### Details

- **Case doesn't matter**: `Auth_Base_Url` and `auth_base_url` are the same.
- **`__` = `:`**: in the mapping you can write `sa__scope__api__auth__base_url` — same as `sa:scope:api:auth:base_url`.
- **An empty key or an empty string in the mapping** — a startup error. Such an alias would never match anything, so it's better to fail fast than silently do nothing.
- **An empty mapping** — nothing changes, the pipeline works as usual.

### Cheat sheet

| I want | Setting |
|--------|---------|
| Link a flat key to a nested path | `KeyAliases["path"] = "flat_key"` |
| Flat key wins | default |
| Nested path wins | `CanonicalValueWins = true` |
| Value missing | path isn't touched, the lower-priority source is used |

---

## Using together with Sa.Configuration.PostgreSql

First hook up the standard sources, then the dynamic settings from the database:

```csharp
using Sa.Configuration;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// First, standard sources (appsettings.json, secrets.txt)
builder.Configuration.AddSaConfiguration();

// Then dynamic settings from the database
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM app_settings"
));

var app = builder.Build();
```

If the database stores a setting under a flat PHP key (say, `auth_base_url`), while C# code reads the nested path (`sa:scope:api:auth:base_url`), set the mapping **in the Secrets layer**, not in the PG options. Then it applies to all providers at once:

```csharp
using Sa.Configuration;

builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
    },
});

string? baseUrl = app.Configuration["sa:scope:api:auth:base_url"];
// → the value of the auth_base_url row
```

The flat key remains separately accessible: `app.Configuration["auth_base_url"]`.

---

## Command-line arguments

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

> **`__` as `:`** — a double underscore in a parameter name is the environment-variable-style way of writing the `:` separator. So `--sa__pg__host` is stored and read as `sa:pg:host`. Single underscores are data, not separators. `args["sa__pg__host"]` and `args["sa:pg:host"]` are interchangeable, and typed getters work the same way.

Typed methods return `null` if the parameter is absent or invalid:

| Method | Type | How it converts |
|--------|------|-----------------|
| `GetBool()` | `bool?` | `"true"/"1"/"yes"/"on"` → `true` |
| `GetInt()` | `int?` | `int.TryParse(..., InvariantCulture)` |
| `GetFloat()` | `float?` | same |
| `GetLong()` | `long?` | same |
| `GetTimeSpan()` | `TimeSpan?` | `TimeSpan.TryParse(..., InvariantCulture)` |

Check whether a parameter is present with `args.Contains("key")`. Full dictionary — `args.Parameters`.

---

## Working with secrets directly

Sometimes secrets are needed outside `IConfiguration`. Then use `Secrets` directly:

```csharp
using Sa.Configuration.SecretStore;

// Default chain (top to bottom by priority):
// CLI args → ENV variables → .env → secrets_enc* → secrets.{Env}.txt → secrets.txt
var secrets = Secrets.CreateDefault();

string? result   = secrets.PopulateSecrets("Server={{host}};Pwd={{sa_pg_password}}");
string? password = secrets.GetSecret("sa_pg_password");
```

Custom chain — any `ISecretStore` (`FileSecretStore`, `EnvironmentVariableSecretStore`, `CommandLineArgsSecretStore`, `InMemorySecretStore`):

```csharp
var secrets = new Secrets(new FileSecretStore("my-secrets.txt"));
secrets.AddStore(new InMemorySecretStore().AddSecret("override_key", "override_value"));
// LIFO: the new store has the highest priority
```

You can configure the base file name and environment:

```csharp
Secrets.CreateDefault(new SecretOptions
{
    FileName = "app-secrets.txt",
    EnvironmentName = "Staging"
});
```

`Secrets.GetEnvironmentName()` takes the environment name from `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` / `environment`, defaulting to `Production`.

---

## License

MIT
