# Sa.Configuration

Secure secrets management and command-line argument parsing within the .NET `Microsoft.Extensions.Configuration` ecosystem. Secrets are automatically substituted into configuration without manual application code.

---

## Features

- **Automatic secret substitution**: [`Quick Start`](#quick-start) — `{{key}}` placeholders are replaced with real values from files, environment variables, or command-line arguments
- **Value normalization**: secret values are trimmed and their surrounding quotes (`'`, `"`, `` ` ``) are stripped — see [Encoding](#encoding)
- **Cycle protection**: nested placeholders resolve up to a depth of 3; circular references throw `InvalidOperationException`. Details in [Secrets](#secrets)
- **Optional placeholders**: [`{{?key}}`](#optional-placeholders) — if the secret is not found, the whole string becomes `null` instead of throwing
- **Chained Stores**: [`Secrets`](#secrets) chains any [`ISecretStore`](#secrets) with LIFO priority (the store added last wins); `AddStore` is thread-safe
- **Argument parser**: [`Arguments`](#arguments) supports `--key value`, `--key=value`, `-flag` formats; negative numbers are treated as values; surrounding quotes are stripped; `__` in a parameter name is read as the `:` configuration separator
- **Environments**: automatic loading of [`secrets.{Environment}.txt`](#secret-priority-order) (Development/Staging/Production)
- **Key Aliases**: [`Key Aliases`](#key-aliases) — a C# path like `sa:scope:api:auth:base_url` can be filled by a flat PHP key `auth_base_url`, applied over the merged configuration of every provider
- **Encryption support**: committed `secrets_enc*.txt` files (AES-256-GCM, PBKDF2-SHA256), raw and passphrase keys (`base64:`), clients/environments, `.env` keys and Unicode — see [Encrypted Secrets](#encrypted-secrets)

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

Secrets use LIFO priority — the store added last wins. The default chain, from lowest to highest priority, is:

| # | Source | Example |
|---|--------|---------|
| 1 | `secrets.txt` | plaintext base file |
| 2 | `secrets.{env}.txt` | plaintext environment-specific file |
| 3 | `secrets_enc.txt` | committed encrypted defaults |
| 4 | `secrets_enc.{env}.txt` | committed encrypted per environment |
| 5 | `secrets_enc.{client}.txt` | committed encrypted per client |
| 6 | `secrets_enc.{client}.{env}.txt` | committed encrypted per client and environment |
| 7 | `.env` | local overrides; also holds the decryption key |
| 8 | `.env.{env}` | local per-environment overrides |
| 9 | Environment variables | override every file |
| 10 | Command-line arguments | highest priority |

The first source with a value wins — command line can override everything, environment variables override files, and so on.

`{env}` is the exact value of `Secrets.GetEnvironmentName()` (`DOTNET_ENVIRONMENT` → `ASPNETCORE_ENVIRONMENT` → `environment`, falling back to `Production`). To use short names such as `dev`, set `DOTNET_ENVIRONMENT=dev` — both plaintext and encrypted file names follow it.

---

## Encrypted Secrets

Encrypted files are safe to commit: values are AES-256-GCM encrypted, while key names and comments stay readable for diffs. Enable it by creating `secrets_enc*.txt` files and supplying a key:

```bash
export SA_SECRETS_DECRYPTION_CLIENT=lsr
export SA_SECRETS_DECRYPTION_KEY=...        # or base64:<32-byte key>
# or put the same variables into .env (never commit .env)
```

File names: `secrets_enc.txt`, `secrets_enc.{env}.txt`, `secrets_enc.{client}.txt`, `secrets_enc.{client}.{env}.txt`. The `{env}` part is the exact environment name (`Development`, `Production`, or whatever `DOTNET_ENVIRONMENT` contains).

### Example of an Encrypted File

`secrets_enc.lsr.dev.txt` — the plaintext `secrets.txt` from [Quick Start](#2-secrets-file-secretstxt) encrypted for client `lsr` and environment `dev`:

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

Keys, comments and the header stay readable — only values are ciphertext. The block above was really produced by `do-secrets.sh` from the throwaway passphrase `demo-passphrase-not-a-real-secret`, so `decrypt` / `check` can be tried on it as-is.

The same idea with a raw key: no `iterations` / `salt` entries and `kdf=none` (`SA_SECRETS_DECRYPTION_KEY=base64:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=`):

```
# saenc: v1
# saenc: kdf=none
# saenc: client=lsr
# saenc: env=prod
sa_pg_password=ENC1:fFodkm/LnlPzC6wIu32wkVAvCbXx6th6qG/So7DsX7MSmOUPpRumfOxb
```

Encrypt and verify files with the repository script:

```bash
./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets_enc.lsr.dev.txt
./build-sh/do-secrets.sh decrypt secrets_enc.lsr.dev.txt
./build-sh/do-secrets.sh check
```

A file that exists without a resolvable key fails startup instead of silently falling back to plaintext. See `SecretStore/Readme.md` for the format, per-client keys and failure behaviour.

### Encoding

Everything is UTF-8 end to end; the only non-UTF-8 token inside a file is the Base64 payload.

| Aspect | Behaviour |
|--------|-----------|
| File encoding | UTF-8. A BOM is accepted and ignored (on the first `# saenc: v1` header line and in `.env`). Files without a BOM are decoded as UTF-8 — re-save CP1251/CP866 content as UTF-8. |
| Line endings | LF, CRLF and CR all read correctly; `SecretsCipher` / `do-secrets.sh` always **write** LF (`\n`), so re-encrypting stays diff-stable. |
| Value → ciphertext | `UTF8.GetBytes(value)` → AES-256-GCM → `nonce(12) ‖ tag(16) ‖ ciphertext` → standard padded Base64, on a single line, never wrapped. |
| Value prefix | `ENC1:` marks format version 1. It is not authenticated itself — the **key name** is (`SAENC1\|<key>`), so a blob cannot be moved to another key. |
| Payload length | `4 × ceil((28 + UTF-8 byte count) / 3)` — 9 ASCII bytes (`localhost`) → 52 chars, `superSecret123` (14 bytes) → 56, `Привет` (12 bytes) → 56. |
| Unicode | Keys and values round-trip as UTF-8: `Привет`, `pä£s=word`, emoji. Non-ASCII characters cost more bytes, hence a longer blob. |
| `=` inside values | Fine — a line is split on the **first** `=` only, so `api_key=abc=123` survives encryption and decryption unchanged. |
| Empty values | Encrypted and restored as empty (`empty=`), no error. |
| Spaces | Keys and values are trimmed at both ends (` key = value ` → `key=value`); inner spaces are preserved. |
| Comments | Any line whose trimmed form starts with `#` is copied verbatim. A *source* comment starting with `# saenc:` is rejected as "already encrypted". |
| Header metadata | `client` / `env` are trimmed, must not contain line breaks, and are validated against the file name when the file is loaded. |
| Passphrase | Any UTF-8 text, trimmed — spaces and non-ASCII characters are allowed. |
| Raw key | `base64:` + Base64 of exactly 32 bytes (44 chars with padding). Any other length fails with a `32 bytes` error. |
| Salt | Base64 of 16 random bytes (24 chars) on the `# saenc: salt=` line; its length is checked, so a hand-edited salt breaks decryption. |
| Case sensitivity | Key names, `ENC1:`, `base64:` and header entry names are ordinal — `# saenc: kdf=PBKDF2-SHA256` is not accepted. |
| Placeholder names | `{{key}}` matches `\w+` (Unicode letters, digits, underscore). Hyphens, dots and spaces never match — `{{api-key}}` is left untouched. |
| Substituted values | Trimmed and stripped of surrounding `'`, `"` or `` ` `` quotes before insertion into the configuration string. |

`.env` files (they hold the decryption key) have their own encoding rules: `KEY=VALUE`, `export KEY=VALUE`, `#` comments, an optional UTF-8 BOM, CRLF/LF, surrounding `'`, `"` or `` ` `` quotes, and the escapes `\n`, `\r`, `\t`, `\"`, `\\` inside double quotes. Unknown escapes such as a Windows path `"C:\Users"` keep their backslash.

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

## Key Aliases

Sometimes a setting is stored under a flat, PHP-style key while the C# code reads it as a nested
path. For example, the C# key `sa:scope:api:auth:base_url` corresponds to the flat PHP key
`auth_base_url`. The mapping is declared on `SecretOptions.KeyAliases` (a `KeyAliasOptions`):

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

The alias runs in the final post-secrets stage, **over the merged configuration of every
provider** — so the flat key and the canonical path may live in different sources (for instance,
the flat key in a PostgreSQL source and the canonical path in `appsettings.json`). Reading the
result, both spellings return the same value:

```csharp
string? baseUrl = app.Configuration["sa:scope:api:auth:base_url"]; // → "https://php.example.com"
string? flat    = app.Configuration["auth_base_url"];             // → "https://php.example.com"
```

### Resolution

- A **non-blank** alias value is written to the canonical path by default — the alias wins for
  the canonical key.
- Set `CanonicalValueWins = true` to invert that: a non-blank alias value then fills the
  canonical path **only when it is empty or absent** in the merged configuration, so a value any
  lower-priority provider already filled wins over the alias.

```csharp
builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        CanonicalValueWins = true,   // canonical value wins; alias only fills an empty path
    },
});
```

- A **missing or blank** alias value never touches the canonical path — it keeps its own value
  (if any) or stays absent, so a lower-priority provider answers for it and no value is
  fabricated. A blank value is always treated as "no value".
- The flat alias key is **not** removed: `auth_base_url` stays reachable directly, so PHP-style
  keys can be read while C# code reads the canonical path.

Mapping keys are compared the same way configuration keys are — case-insensitively, and spelled
with `:` when they contain `__` (the environment-variable spelling of the separator) — so
`sa__scope__api__auth__base_url` in the map addresses the same key as
`sa:scope:api:auth:base_url`. An empty or blank mapping entry is a registration-time error: such
an alias can never match a setting, so it fails while the host is still building rather than
silently doing nothing. An empty map leaves the pipeline exactly as it was before the feature.

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

When a setting is stored in the database under a flat PHP-style key (e.g. `auth_base_url`)
instead of the nested C# path (`sa:scope:api:auth:base_url`), declare the mapping in the Secrets
layer — **not** on the PG options — so it applies over the merged configuration of every provider
(see [Key Aliases](#key-aliases)):

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

The flat key stays reachable as its own key (`app.Configuration["auth_base_url"]`).

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

> **`__` as `:`** — a double underscore in a parameter name is the environment-variable spelling
> of the `:` separator, so `--sa__pg__host` is stored and read as `sa:pg:host` (single underscores
> are data). `args["sa__pg__host"]` and `args["sa:pg:host"]` are interchangeable, and the typed
> getters follow the same rule.

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

// Default chain (LIFO): CLI args → env vars → .env → secrets_enc* → secrets.{Env}.txt → secrets.txt
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
