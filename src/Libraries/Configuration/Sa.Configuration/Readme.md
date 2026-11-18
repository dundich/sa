# Sa.Configuration

**English** | [Русский](Readme-ru.md)

A .NET package that assembles and extends configuration:

1. **Builds the layer stack** — static sources (appsettings, environment variables, arguments, secrets) and dynamic ones (a database) in one deterministic order, with a builder for the two strategies.
2. **Substitutes secrets into configuration** — instead of hardcoding passwords and keys, you write `{{sa_pg_password}}` in the config, and the package fills in the real value from a file, an environment variable, or a command-line argument.
3. **Overlays per-client settings** — every client has its own configuration root with the same key names, laid over the shared values, and is addressable as a keyed `IConfiguration`.
4. **Reloads only the dynamic layer** — `ISaConfigurationReloader` refreshes the database sources (and the values derived from them) without re-reading the files, and registers no background service unless you ask for one.
5. **Parses command-line arguments** — `--key value`, `--key=value`, `-flag`, and so on.

It plugs into the standard `Microsoft.Extensions.Configuration` pipeline, so it slots right into a normal ASP.NET setup.

## Installation

```bash
dotnet add package Sa.Configuration
```

---

## Contents

- [Installation](#installation)
- [What it can do](#what-it-can-do)
- [Layers and the builder](#layers-and-the-builder)
- [Quick start](#quick-start)
  - [1. Wire up the package in `Program.cs`](#1-wire-up-the-package-in-programcs)
  - [2. Create a secrets file `secrets.txt`](#2-create-a-secrets-file-secretstxt)
  - [3. Write placeholders in `appsettings.json`](#3-write-placeholders-in-appsettingsjson)
  - [4. Read the configuration as usual](#4-read-the-configuration-as-usual)
- [Where secrets come from (priority order)](#where-secrets-come-from-priority-order)
- [Encrypted secrets](#encrypted-secrets)
  - [Example encrypted file](#example-encrypted-file)
  - [Encodings](#encodings)
- [Optional placeholders](#optional-placeholders)
- [Key aliases](#key-aliases)
  - [The problem](#the-problem)
  - [How to set it](#how-to-set-it)
  - [Several registrations (multisetup)](#several-registrations-multisetup)
  - [The alias must be the last word](#the-alias-must-be-the-last-word)
  - [Custom transforms](#custom-transforms)
  - [What happens after that](#what-happens-after-that)
  - [Where the value is looked up](#where-the-value-is-looked-up)
  - [Who overrides whom](#who-overrides-whom)
  - [Details](#details)
  - [Cheat sheet](#cheat-sheet)
- [Use with Sa.Configuration.PostgreSql](#use-with-saconfigurationpostgresql)
- [Clients (core + per-client overlays)](#clients-core--per-client-overlays)
- [Dynamic reload](#dynamic-reload)
- [Full DI example](#full-di-example)
- [Command-line arguments](#command-line-arguments)
- [Working with secrets directly](#working-with-secrets-directly)
- [License](#license)

## What it can do

- **Automatic secret substitution** — `{{key}}` placeholders are replaced with real values. See [Quick start](#quick-start).
- **Value cleanup** — values are trimmed at the edges and stripped of surrounding quotes (`'`, `"`, `` ` ``). Details in [Encodings](#encodings).
- **Cycle protection** — nested placeholders are expanded up to 3 levels deep. If a cycle forms, you get a clear `InvalidOperationException`. Details in [Secrets](SecretStore/Readme.md).
- **Optional placeholders** — `{{?key}}` instead of `{{key}}`: if the secret is missing, the string becomes `null` instead of failing with an error.
- **Multiple stores at once** — [`Secrets`](SecretStore/Readme.md) can combine any number of stores (`ISecretStore`). The one added last wins. Stores can be added at runtime, including from multiple threads.
- **Argument parsing** — [`Arguments`](CommandLine/Readme.md) understands `--key value`, `--key=value`, `-flag`. Negative numbers are treated as values, not flags. Quotes are stripped. `__` in a parameter name is read as the `:` separator.
- **Environments** — automatically picks up `secrets.{Environment}.txt` for Development/Staging/Production.
- **Key aliases** — [Key aliases](#key-aliases): you can link a "flat" setting name (say, `auth_base_url` from the PHP world) to a nested C# path (`sa:scope:api:auth:base_url`). Works across all sources at once.
- **Encryption** — encrypted `secrets*.pub` files (AES-256-GCM) can be committed to the repo. Encryption keys are plain or `base64:`. Clients, environments, `.env`, and Unicode are supported. See [Encrypted secrets](#encrypted-secrets).
- **Layer builder** — `AddSaConfiguration(sa => ...)` assembles static and dynamic sources in a fixed order. See [Layers and the builder](#layers-and-the-builder).
- **Per-client configuration** — the core holds the shared keys, every client gets its own root with the same key names, built from the flat rows the providers return and reachable as a keyed `IConfiguration`. See [Clients](#clients-core--per-client-overlays).
- **Dynamic reload** — only the database sources are refreshed, and the aliases/placeholders are recomputed from the fresh values. No timer by default. See [Dynamic reload](#dynamic-reload).

---

## Layers and the builder

Configuration is assembled from two kinds of sources:

| Layer | Typical sources | How it changes at runtime |
|-------|-----------------|---------------------------|
| **Static** | `appsettings.json`, `appsettings.{environment}.json`, environment variables, command-line arguments, `secrets*.txt` | a file edit (`reloadOnChange`), an explicit `IConfigurationRoot.Reload()`, or a restart |
| **Dynamic** | PostgreSQL, or any `ISaDynamicConfigurationSource` | `ISaConfigurationReloader.ReloadDynamic[Async]()` — and only this layer is re-read |

`AddSaConfiguration(sa => ...)` is the builder. It keeps one rule: **later sources override earlier ones**, and the secret/alias pass is always kept after every source registered through it.

```csharp
// Strategy 1 — static only: files, environment, arguments, secrets.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseStandardSources();   // appsettings.json → appsettings.{env}.json → env vars → args
    sa.UseSecrets();           // {{placeholder}} + key aliases
});
```

```csharp
// Strategy 2 — static + dynamic core. Clients are registered separately (see Clients below).
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseStandardSources();                                                // static
    sa.UseSecrets();                                                        // {{…}} + aliases
    sa.UseDynamic(new DatabaseConfigurationSource(                          // dynamic, overrides static
        new PostgreSqlConfigurationOptions("SELECT key, value FROM setting"),
        PgConfigurationConnection.FromServices()));
});
```

| Method | What it adds |
|--------|--------------|
| `UseStandardSources(...)` | `appsettings.json`, `appsettings.{env}.json`, environment variables, arguments. **Idempotent**: in ASP.NET Core / generic host the host already added them, so the method is a no-op there. |
| `UseSecrets(...)` | the existing secret pipeline: placeholders, key aliases, the command-line source. |
| `UseDynamic(source)` | a source whose values change at runtime; placed after the static ones. |
| `Use(source)` | any `IConfigurationSource`, for the few sources without a dedicated method. |
| `Finalize()` | moves the pass after everything (needed only when sources are added to `sa.Builder` directly). |

A source registered after the last Sa call is a normal provider and beats the pass for the keys it holds; `Finalize()` (or `sa.Builder.Add(...)` followed by `Finalize()`) is the explicit way to restore the order.

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
| 4 | `secrets.{client}.{env}.pub` / `secrets.{client}.pub` | encrypted secrets committed to the repo |
| 5 | `secrets.{env}.pub` / `secrets.pub` | encrypted default values |
| 6 | `secrets.{env}.txt` | regular environment-specific file |
| 7 | `secrets.txt` | regular base file |

Put simply: the command line overrides everything, environment variables override files, and environment-specific files override the base file.

What `{env}` means: the environment name taken from `DOTNET_ENVIRONMENT` → `ASPNETCORE_ENVIRONMENT` → `environment`. If none is set — `Production`. Want a short name like `dev`? Set `DOTNET_ENVIRONMENT=dev`, and all file names will follow it.

---

## Encrypted secrets

Encrypted files can be safely committed to the repo: values are encrypted with AES-256-GCM, while key names and comments stay readable — convenient for diffs.

How to use:

1. Create `secrets*.pub` files.
2. Set the decryption key:

```bash
export SA_SECRETS_DECRYPTION_CLIENT=lsr
export SA_SECRETS_DECRYPTION_KEY=...        # or base64:<32-byte key>
# the same variables can go into .env (the .env itself is not committed)
```

Possible file names:

- `secrets.pub`
- `secrets.{env}.pub`
- `secrets.{client}.pub`
- `secrets.{client}.{env}.pub`

`{env}` is the exact environment name (`Development`, `Production`, or whatever is in `DOTNET_ENVIRONMENT`).

The `.pub` extension marks a file that is safe to commit; the contents are AES-256-GCM ciphertext, not a public key.

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
./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets.lsr.dev.pub
./build-sh/do-secrets.sh decrypt secrets.lsr.dev.pub
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

### Several registrations (multisetup)

Calling `AddSaConfiguration` more than once is supported and adds up:

```csharp
builder.Configuration
    .AddSaConfiguration(new SecretOptions { KeyAliases = { ["sa:…:base_url"] = "auth_base_url" } })
    .AddSaConfiguration(new SecretOptions { KeyAliases = { ["sa:…:token_ttl"] = "auth_token_ttl" } });
```

- every call **merges** its `KeyAliases` into one accumulated map; for a canonical key declared twice the **last** call wins;
- the secret options (`FileName`, `LoadDotEnv`, `Encryption`, `Args`) also come from the **last** call;
- only **one** pass and **one** command-line source are registered, so secrets are read once, on the first build (registration itself doesn't touch the files);
- `CanonicalValueWins` is a single flag, so its value is taken from the last call as well.

### The alias must be the last word

The pass derives values and therefore wins over every provider registered **before** it. A provider added **after** the last `AddSaConfiguration` is an ordinary provider and beats the alias for the keys it holds. Call `FinalizeSaConfiguration()` as the last configuration step when sources are registered later:

```csharp
builder.Configuration
    .AddSaPostgreSqlConfiguration(...)   // a source holding canonical keys
    .AddSaConfiguration(...)             // aliases
    .FinalizeSaConfiguration();          // the pass goes after everything
```

### Custom transforms

The final pass is extensible: `AddSaConfigurationTransform` appends a step that receives the merged configuration and returns **only** the keys it changes. Transforms run after the built-in ones (placeholders, then aliases) in registration order, and each one sees the previous ones' output:

```csharp
public sealed class StripPrefixTransform(string prefix) : ISaConfigurationTransform
{
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
        => configuration.Values
            .Where(c => c.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(c => new KeyValuePair<string, string?>(c.Key[prefix.Length..], c.Value));
}

builder.Configuration.AddSaConfigurationTransform(new StripPrefixTransform("legacy:"));
```

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
- **`Reload()`** — the pass is recomputed: a key that disappeared from its source or from the mapping stops being served.

### Cheat sheet

| I want | Setting |
|--------|---------|
| Link a flat key to a nested path | `KeyAliases["path"] = "flat_key"` |
| Flat key wins | default |
| Nested path wins | `CanonicalValueWins = true` |
| Value missing | path isn't touched, the lower-priority source is used |

---

## Use with Sa.Configuration.PostgreSql

The PostgreSQL package adds a dynamic source. Give it a connection strategy, then keep the Sa
pass after it — `AddSaPostgreSqlConfiguration` already does that for you:

```csharp
using Sa.Configuration;
using Sa.Configuration.PostgreSql;

// The application's own pool (or NpgsqlDataSource): nothing extra is created.
builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));

// Or an explicit pool, or a connection string owned by the source:
builder.Configuration.AddSaPostgreSqlConfiguration(
    appDataSource, new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
builder.Configuration.AddSaPostgreSqlConfiguration(
    "Host=db;Database=app;Username=app;Password=…",
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
```

Aliases declared in the secret layer apply on top of the database values, and the database layer
overrides the static one (it is registered later). See the package README for the query shapes and
the reload details.

---

## Clients (core + per-client overlays)

The core configuration keeps the shared, fixed set of keys. A client does not prefix anything: it
gets its **own configuration root** with the **same key names**, laid over the core values. Reading a
client resolves its own value first and falls back to the core one.

```text
core ("s3_url" = "A")                 ← shared value
client1 ("s3_url" = "B1")             ← separate root, same key name
client2 ("s3_url" = "C")              ← separate root, same key name

cf["s3_url"]      → "A"
clients.GetClient("client1")["s3_url"] → "B1"
clients.GetClient("client2")["s3_url"] → "C"
```

There is no `sa:clients:{client}:{key}` prefix and no shared dictionary: the client roots are
independent, so one client's values can never leak into the core or into another client. Key names
are a convention, not a schema — a client may introduce keys the core does not have.

### Data: core table + `client_settings`

The canonical shape is two tables. The shared values live in `setting`; each client's overrides live
in `client_settings`, split by `client_id`:

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

### Registration: providers returning flat rows

A provider is a callback registered with `clients.Add<T1[,T2[,T3]]>(...)`. Its one to three
parameters are resolved from the container when the rows are read (plus a `CancellationToken`), and
it returns flat `(client_id, key, value)` rows for **every** client at once. The registry groups the
rows by `client_id` and builds one root per client, so a provider knows nothing about client roots,
splitters or prefixes — it just returns rows.

```csharp
using Sa.Configuration.Clients;

builder.Services.AddSingleton<IClientSettingsStore, PgClientSettingsStore>();

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    // Up to three dependencies come from DI; the callback returns rows for all clients.
    clients.Add<IClientSettingsStore>(
        (store, cancellationToken) => store.LoadAllAsync(cancellationToken));
});
```

The row type is `ClientSetting(clientId, key, value)` — exactly the shape a query returns:

```csharp
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
```

A client needs no separate declaration: it exists as soon as any provider returns a row for it. The
providers are called lazily on first access and again on `ReloadAsync()` /
`ReloadDynamicAsync()`, so a new row in `client_settings` becomes addressable without a restart.

Several providers can be registered and are merged per client; a later row for the same client and
key wins:

```csharp
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IClientSettingsStore>((store, ct) => store.LoadAllAsync(ct));

    clients.Add<IDefaultsStore, ILogger<Clients>>(
        (defaults, logger, ct) => defaults.LoadAsync(logger, ct));
});
```

### Clients from a configuration section (no database)

When the clients are already in static configuration, `UseConfiguration` registers the same kind of
provider over a section. The children of the section are the client ids; the keys inside a client's
section use the same names as the core configuration and are read **without** the section prefix.

```json
{
  "s3_url": "A",
  "theme": "dark",
  "language": "en",

  "clients": {
    "acme": { "s3_url": "B1", "theme": "light" },
    "beta": { "s3_url": "C", "language": "de" }
  }
}
```

```csharp
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
    clients.UseConfiguration(builder.Configuration));
```

`clients.Clients` → `["acme", "beta"]`; `clients.GetClient("acme")["s3_url"]` → `"B1"`;
`clients.GetClient("acme")["language"]` → `"en"` (the core value). The registry is live: a client
added to the section appears after a reload, and existing client values are re-read on every
`ReloadDynamicAsync()`.

A different layout — only the section path changes:

```csharp
clients.UseConfiguration(builder.Configuration, new ConfigurationClientSourceOptions
{
    SectionPath = "tenants",        // tenants:acme:s3_url, tenants:beta:s3_url, …
});
```

`UseConfiguration` is a provider like any other, so it can be combined with `clients.Add<T>(...)`;
the rows from all providers are merged per client.

### DI variants

| Shape | When to use |
|---|---|
| `clients.Add<T1>((t1, ct) => rows)` | the provider needs one DI service and returns all clients' rows |
| `clients.Add<T1, T2>((t1, t2, ct) => rows)` | two services (a store and a logger, say) |
| `clients.Add<T1, T2, T3>((t1, t2, t3, ct) => rows)` | three services |
| `clients.UseConfiguration(configuration)` | the clients and their values live in a configuration section (`clients:{id}:{key}`), no database involved |
| `AttachAsync(client, source)` | a client appears at runtime (a plugin, a manually provisioned tenant, a test) |
| `clients.Configure(o => …)` | adjust `ThrowForUnknownClient` / `EagerLoad` |

### Reading

```csharp
public sealed class SettingsEndpoints(IConfiguration core, IClientConfigurations clients)
{
    public string CoreS3Url => core["s3_url"]!;                       // "A"
    public string Client1S3Url => clients.GetClient("client1")["s3_url"]!; // "B1"
    public string Client2S3Url => clients.GetClient("client2")["s3_url"]!; // "C"

    public IReadOnlyCollection<string> Known => clients.Clients;       // from providers + attached
}
```

Rules:

- a client value wins over the core value of the same key; a missing client value falls back to the
  core one and is never invented;
- a client sees only its own root plus the core — never another client's values; with the
  section-based provider the clients section is meta-data of the overlay and is hidden from every
  view, so one client cannot read another client's rows through the core fallback;
- a blank row (a `value` that is empty or whitespace) is dropped while the rows are read and
  delegates to the core value — `ClientConfigurationOptions.SkipEmptyValues`, on by default, mirrors
  the core database layer; set it to `false` to make a blank an explicit value of its own. A blank a
  transform produced is written after the rows are read and always stays local to the client;
- an unknown client reads the core values; `TryGet` reports whether it is actually known and never
  throws, not even with `ThrowForUnknownClient` (only `GetClient` does);
- `Clients` is the union of the ids the providers returned and the runtime attachments, so a client
  added to the database appears after `ReloadDynamicAsync()`;
- **rows are rewritten by the same pass as the core** — a client's values go through the transform
  chain too, so `{{placeholder}}` secrets are substituted and key aliases are resolved inside the
  client's own rows (alias sources are read from the client first, the core second). A derived value
  that only mirrors the core is not copied into the overlay, so the client keeps reading the core
  live. A row the pass cannot resolve (a missing secret) reads as a blank for that client, not as
  the core value and not as the literal `{{…}}`. The section-based provider (`UseConfiguration`) is
  the exception: the core pass blanks an unresolved placeholder **before** the section is read, so
  that row never reaches the registry and the client falls back to the core value;
- keys use the canonical `:` spelling: `sa__pg__host` in a row is the same setting as `sa:pg:host`.

Sections work across both roots:

```csharp
IConfiguration db = clients.GetClient("client1").GetSection("db");
db["host"]; // client1's value when present, otherwise the core value
```

### Aliases and placeholders inside a client overlay

A client's rows go through the same pass as the core, so a client can keep its settings under
whatever flat names its own system uses:

```csharp
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets(new SecretOptions
    {
        KeyAliases = new KeyAliasOptions
        {
            KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        },
    });
});

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
    clients.Add<IClientSettingsStore>((store, ct) => store.LoadAllAsync(ct)));
```

```text
core:    auth_base_url = https://core.example.com   → sa:scope:api:auth:base_url = https://core.example.com
client1: auth_base_url = https://acme.example.com   → sa:scope:api:auth:base_url = https://acme.example.com
client2: (no such row)                              → https://core.example.com, read live from the core

client1: api_token = {{acme_token}}                 → the resolved secret, never the literal
```

The alias source is looked up in the client first and in the core second, and a client's value never
crosses into the core or into another client. A derived value that only mirrors the core is not
copied into the overlay, so a change in the core is visible without reloading the client rows.

### Keyed injection

When the client id is known at compile time, resolve its configuration by key — the handler or
constructor receives the client's own root directly.

```csharp
using Microsoft.Extensions.DependencyInjection;   // [FromKeyedServices]

public static class Clients
{
    public const string Acme = "acme";
    public const string Beta = "beta";
}

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
    clients.UseConfiguration(builder.Configuration));

// Minimal API: the known client comes from the container, no registry lookup in the body.
app.MapGet("/acme/settings", ([FromKeyedServices(Clients.Acme)] IConfiguration acme) => new
{
    S3Url = acme["s3_url"],        // "B1"
    Theme = acme["theme"],         // "light"
    Language = acme["language"],   // "en" — core fallback
});

// A regular service: the same keyed injection in the constructor.
public sealed class AcmeStorage([FromKeyedServices(Clients.Acme)] IConfiguration configuration)
{
    public string S3Url => configuration["s3_url"]!;
}

// Code that holds an IServiceProvider (a factory, a startup task):
IConfiguration beta = app.Services.GetRequiredKeyedService<IConfiguration>(Clients.Beta);
beta["s3_url"];                    // "C"
```

The key is the client id used in `client_settings.client_id` (or the section's child key) and is
matched case-insensitively. Injection is per resolution — a client attached or refreshed later is
visible on the next request without re-registering anything.

A name known only at runtime cannot be a compile-time key, so pass the id and ask the registry:

```csharp
app.MapGet("/settings/{client}", (string client, IClientConfigurations clients) =>
    new { Client = client, S3Url = clients.GetClient(client)["s3_url"] });
```

The keyed registration uses `KeyedService.AnyKey`, so `GetRequiredKeyedService<IConfiguration>(id)`
works for a name that only exists at runtime. Enumerating keyed services does not enumerate clients —
use `IClientConfigurations.Clients` for that.

### Runtime attach and removal

```csharp
await clients.AttachAsync("acme", new MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?> { ["s3_url"] = "B1" },
});

await clients.RemoveAsync("acme");   // the view falls back to the core again
```

`AttachAsync` replaces an existing overlay of the same client and disposes the replaced one.
`RemoveAsync` returns `false` and changes nothing for a client a provider still reports — its overlay
would be rebuilt on the next access, so the call reports that there was nothing to remove. Only a
runtime attachment can be removed.

Views returned by `GetClient` are live: they ask the registry for the current root on every access,
so an attach/remove/reload is visible through a reference obtained earlier — including a view of a
client that was unknown when the reference was obtained. The same goes for the change token:
`GetReloadToken()` on a view composes the core, the overlay and the registry, so a callback fires on
an attach or a remove as well, not only on a reload. The registry is thread-safe.

---

## Dynamic reload

`IConfigurationRoot.Reload()` reloads **every** provider, including the files on disk. When only the
database layer should be refreshed, use the runtime:

```csharp
ISaConfigurationReloader reloader = app.Services.GetRequiredService<ISaConfigurationReloader>();

await reloader.ReloadDynamicAsync(cancellationToken);
```

What it does: reloads the dynamic providers, then recomputes the post-processing pass (placeholders,
aliases, custom transforms) from the fresh values, then raises the reload tokens. Static providers
are not touched; their file watchers keep working as usual.

Getting the service — and whether anything runs in the background:

```csharp
builder.Services.AddSaConfigurationRuntime(builder.Configuration);                 // manual only, no hosted service
builder.Services.AddSaConfigurationRuntime(builder.Configuration, o =>
{
    o.InitialLoad = SaDynamicInitialLoad.Hosted;      // one-shot load before the server starts
    o.DynamicReload = true;                           // periodic polling — opt-in
    o.DynamicReloadInterval = TimeSpan.FromSeconds(15);
});
```

| Option | Default | Meaning |
|--------|---------|---------|
| `InitialLoad` | `Explicit` | `Explicit` — nothing happens until you call `EnsureInitializedAsync`; `Hosted` — a one-shot hook loads before the first request and a failure fails the start; `Background` — the host starts first, the load is logged if it fails |
| `DynamicReload` | `false` | registers exactly one hosted service with a `PeriodicTimer`; without it no timer exists |
| `DynamicReloadInterval` | 30 s | polling period |
| `DynamicReloadInitialDelay` | `null` | delay before the first poll (one interval by default) |

A late-bound connection (`PgConfigurationConnection.FromServices()`) is resolved from the container
during `EnsureInitializedAsync`, i.e. after `builder.Build()`; with an explicit
`DataSource`/`ConnectionString` no initialization step is needed. Initialization is idempotent, and a
failed periodic reload is logged while the previous snapshot stays in effect.

---

## Full DI example

Static + dynamic core + two clients, with one connection pool and no background service unless asked:

```csharp
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Static: the host already added appsettings/env/args, so UseStandardSources is a no-op here.
// The dynamic source reads the shared values through the pool registered below.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets();                                   // {{placeholder}} + key aliases
    sa.UseDynamic(new DatabaseConfigurationSource(
        new PostgreSqlConfigurationOptions("SELECT key, value FROM setting"),
        PgConfigurationConnection.FromServices("orders")));
});

builder.Services.AddSaPostgreSqlDataSource("orders", b => b.FromConfiguration("Postgres"));
builder.Services.AddSingleton<IClientSettingsStore, PgClientSettingsStore>();

// Every client gets its own root; keys are stored verbatim, no prefix. The provider resolves its
// store from DI and returns `(client_id, key, value)` rows for all clients at once.
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IClientSettingsStore>((store, ct) => store.LoadAllAsync(ct));
});

builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.InitialLoad = SaDynamicInitialLoad.Hosted;   // before the first request
});

var app = builder.Build();

app.MapGet("/settings", (IConfiguration configuration, IClientConfigurations clients) => new
{
    Theme = configuration["theme"],                     // core value
    S3Url = configuration["s3_url"],                    // core value
    Clients = clients.Clients,                          // ["acme", "beta"]
});

app.MapGet("/settings/{client}", (string client, IClientConfigurations clients) =>
{
    IConfiguration view = clients.GetClient(client);
    return new
    {
        Client = client,
        Theme = view["theme"],
        S3Url = view["s3_url"],
        Language = view["language"],
    };
});

app.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
    await reloader.ReloadDynamicAsync());

app.Run();
```

The provider behind `clients.Add<IClientSettingsStore>(...)` — one query, flat rows, grouped by the
registry:

```csharp
public interface IClientSettingsStore
{
    ValueTask<IReadOnlyList<ClientSetting>> LoadAllAsync(CancellationToken ct);
}

public sealed class PgClientSettingsStore(IPgDataSource dataSource) : IClientSettingsStore
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
```

Sample data — the core table and the per-client table:

```sql
CREATE TABLE setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);

INSERT INTO setting (key, value) VALUES
    ('s3_url',   'A'),      -- core value for every client
    ('theme',    'dark'),
    ('language', 'en');

CREATE TABLE client_settings (
    client_id TEXT NOT NULL,
    key       TEXT NOT NULL,
    value     TEXT NOT NULL,
    PRIMARY KEY (client_id, key)
);

INSERT INTO client_settings (client_id, key, value) VALUES
    ('acme', 's3_url',   'B1'),   -- acme overrides only its s3_url
    ('acme', 'theme',    'light'),
    ('beta', 's3_url',   'C'),    -- beta has its own value too
    ('beta', 'language', 'de');
```

`GET /settings` → `s3_url` = `A`, `theme` = `dark`, `language` = `en`;
`GET /settings/acme` → `B1` / `light` / `en`;
`GET /settings/beta` → `C` / `dark` / `de`;
`GET /settings/unknown` → `A` / `dark` / `en`.

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
// CLI args → ENV variables → .env → secrets*.pub → secrets.{Env}.txt → secrets.txt
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
