# Sa.Configuration

**English** | [Русский](Readme-ru.md)

A .NET package that assembles and extends configuration:

1. **Builds the layer stack** — static sources (appsettings, environment variables, arguments, secrets) and dynamic ones (a database) in one deterministic order, with a builder for the two strategies.
2. **Substitutes secrets into configuration** — instead of hardcoding passwords and keys, you write `{{sa_pg_password}}` in the config, and the package fills in the real value from a file, an environment variable, or a command-line argument.
3. **Overlays per-client settings** — `sa:clients:{client}:{key}` values are laid over the shared ones, and every client is addressable as a keyed `IConfiguration`.
4. **Reloads only the dynamic layer** — `ISaConfigurationReloader` refreshes the database sources (and the values derived from them) without re-reading the files, and registers no background service unless you ask for one.
5. **Parses command-line arguments** — `--key value`, `--key=value`, `-flag`, and so on.

It plugs into the standard `Microsoft.Extensions.Configuration` pipeline, so it slots right into a normal ASP.NET setup.

---

## Contents

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
- [Clients (keyed configuration)](#clients-keyed-configuration)
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
- **Per-client overlay** — `sa:clients:{client}:{key}` in any source, a keyed `IConfiguration` per client, and a factory for names known only at runtime. See [Clients](#clients-keyed-configuration).
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
// Strategy 2 — static + dynamic + clients.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseStandardSources();                                                // static
    sa.UseSecrets();                                                        // {{…}} + aliases
    sa.UseDynamic(new DatabaseConfigurationSource(                          // dynamic, overrides static
        new PostgreSqlConfigurationOptions("SELECT client_id, key, value FROM settings")
        {
            ClientColumn = "client_id",
        },
        PgConfigurationConnection.FromServices()));
    sa.UseClients();                                                        // sa:clients:{client}:{key}
});
```

| Method | What it adds |
|--------|--------------|
| `UseStandardSources(...)` | `appsettings.json`, `appsettings.{env}.json`, environment variables, arguments. **Idempotent**: in ASP.NET Core / generic host the host already added them, so the method is a no-op there. |
| `UseSecrets(...)` | the existing secret pipeline: placeholders, key aliases, the command-line source. |
| `UseDynamic(source)` | a source whose values change at runtime; placed after the static ones. |
| `UseClients(path = "sa:clients")` | the per-client overlay, including alias resolution inside each client prefix. |
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
overrides the static one (it is registered later). See the package README for the query shapes,
`ClientColumn`, and the reload details.

---

## Clients (keyed configuration)

Base values are shared; a client overrides only what it needs. The convention is one key prefix —
`sa:clients:{client}:{key}` — so client data can live in **any** source: JSON, environment
variables, command-line arguments or a database. No extra provider, no extra pool.

```text
theme                          ← base value, used when the client has no override
sa:clients:acme:theme          ← override for "acme"
sa:clients:beta:theme          ← override for "beta"
```

### Static data: several clients in `appsettings.json`

```json
{
  "theme": "dark",
  "language": "en",
  "sa": {
    "clients": {
      "acme": { "theme": "light" },
      "beta": { "language": "de", "theme": "dark" }
    }
  }
}
```

The same data through the other static sources:

```bash
# environment variables: __ is the ":" separator
export SA__CLIENTS__ACME__THEME=light

# command line
./app --sa__clients__beta__language=de
```

### Dynamic data: SQL that returns every client at once

```sql
-- One query, one connection pool, every client. client_id NULL holds the base values.
SELECT client_id, key, value FROM settings;
```

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.FromServices(),
    new PostgreSqlConfigurationOptions("SELECT client_id, key, value FROM settings")
    {
        ClientColumn = "client_id",                 // first column: client, key, value
        ClientsSection = "sa:clients",              // the default; kept explicit here
    });
```

A pre-filtered query works too — the constant client tells the source which prefix to use:

```sql
SELECT key, value FROM settings WHERE client_id = @client_id;
```

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    connectionString,
    new PostgreSqlConfigurationOptions(
        "SELECT key, value FROM settings WHERE client_id = @client_id",
        new NpgsqlParameter("client_id", "acme"))
    {
        Client = "acme",                            // → sa:clients:acme:{key}
    });
```

Prefer the single query: it is the only shape where a new client appears after a reload without a
restart (a filtered query needs one source per client, fixed at build time).

### Reading a client: computed keys

A client key is an ordinary configuration path, so it can be computed instead of hardcoded. This is
the same shape the SQL `ClientColumn` produces:

```csharp
using Sa.Configuration.Clients;

// One place that knows the convention — no scattered "sa:clients:…" literals.
static string ClientKey(string client, string setting)
    => $"{ClientConfiguration.DefaultSectionPath}:{client}:{setting}";

string? acmeTheme = configuration[ClientKey("acme", "theme")];   // "light"
string? betaTheme = configuration[ClientKey("beta", "theme")];   // null: beta has no override
```

The overlay is usually more convenient, because it adds the fallback to the base value:

```csharp
IConfiguration acme = configuration.ForClient("acme");

acme["theme"];          // "light"  — the override
acme["language"];       // "en"     — the base value
acme["database:host"];  // sections work as usual: acme's child wins over the base child
```

And when the setting names are dynamic as well, compute both parts:

```csharp
string[] settings = ["theme", "language", "notifications"];

foreach (string client in configuration.GetClientNames())
{
    IConfiguration view = configuration.ForClient(client);

    foreach (string setting in settings)
    {
        // view[setting] is the overlay; the computed key reads the raw prefixed value.
        string? value = view[setting];
        string raw = ClientKey(client, setting);

        Console.WriteLine($"{client}:{setting} = {value ?? "(absent)"} (raw {raw})");
    }
}
```

Rules:

- a client value wins over the base value of the same path; a missing client value falls back to the
  base one (it is never invented);
- clients never see each other, and the overrides section itself is hidden from a client view;
- an unknown client simply reads the base values — `ForClient` never throws for it.
  `IClientConfigurationFactory.TryFor` reports whether the client is actually declared;
- `GetClientNames()` / `IClientConfigurationFactory.Clients` are recomputed on every read, so a client
  added to the database appears after `ReloadDynamicAsync()`.

### Dependency injection

```csharp
builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.KeyedClientConfigurations = true;     // keyed IConfiguration + IClientConfigurationFactory
});
```

A compile-time name — keyed injection:

```csharp
app.MapGet("/acme/theme", ([FromKeyedServices("acme")] IConfiguration acme) => acme["theme"]);
```

A name known only at runtime — keyed resolution or the factory:

```csharp
app.MapGet("/settings/{client}", (string client, HttpContext http) =>
    http.RequestServices.GetRequiredKeyedService<IConfiguration>(client)["theme"]);

// Every client, discovered from the overrides:
app.MapGet("/settings", (IClientConfigurationFactory clients) =>
    clients.Clients.Select(id => new { Client = id, Theme = clients.For(id)["theme"] }));
```

`For`/`ForClient` returns a thin, live view: only the prefix is cached, never a value, so a reload is
visible immediately and one instance is safe for every request.

### Aliases inside a client prefix

`UseClients()` teaches the alias pass to work per client, so a client can keep its settings under
whatever flat names its own system uses:

```csharp
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseClients();
    sa.UseSecrets(new SecretOptions
    {
        KeyAliases = new KeyAliasOptions
        {
            KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        },
    });
});
```

```text
sa:clients:acme:auth_base_url                      → sa:clients:acme:sa:scope:api:auth:base_url
```

Values never cross the prefix boundary: the base alias keeps the base value, each client resolves
its own, and one client's flat key cannot leak into another client or into the base configuration.

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

Static + dynamic + two clients, with one connection pool and no background service unless asked:

```csharp
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Static: the host already added appsettings/env/args, so UseStandardSources is a no-op here.
// The dynamic source reads through the pool registered below.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets();                                   // {{placeholder}} + key aliases
    sa.UseDynamic(new DatabaseConfigurationSource(
        new PostgreSqlConfigurationOptions("SELECT client_id, key, value FROM settings")
        {
            ClientColumn = "client_id",
        },
        PgConfigurationConnection.FromServices("orders")));
    sa.UseClients();
});

builder.Services.AddSaPostgreSqlDataSource("orders", b => b.FromConfiguration("Postgres"));
builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.KeyedClientConfigurations = true;
    runtime.InitialLoad = SaDynamicInitialLoad.Hosted;   // before the first request
});

var app = builder.Build();

app.MapGet("/settings", (IConfiguration configuration) => new
{
    Theme = configuration["theme"],                     // base value
    Clients = configuration.GetClientNames(),           // ["acme", "beta"]
});

app.MapGet("/settings/{client}", (string client, IConfiguration configuration) =>
{
    IConfiguration view = configuration.ForClient(client);
    return new { Client = client, Theme = view["theme"], Language = view["language"] };
});

app.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
    await reloader.ReloadDynamicAsync());

app.Run();
```

`settings` sample data:

```sql
CREATE TABLE settings (client_id TEXT, key TEXT NOT NULL, value TEXT NOT NULL);

INSERT INTO settings (client_id, key, value) VALUES
    (NULL,   'theme',    'dark'),   -- base value for everyone
    (NULL,   'language', 'en'),
    ('acme', 'theme',    'light'),  -- acme overrides only the theme
    ('beta', 'language', 'de');     -- beta overrides only the language
```

`GET /settings` → base `dark`/`en`; `GET /settings/acme` → `light`/`en`;
`GET /settings/beta` → `dark`/`de`; `GET /settings/unknown` → `dark`/`en`.

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
