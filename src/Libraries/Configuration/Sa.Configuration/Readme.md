# Sa.Configuration

**English** | [Русский](Readme-ru.md)

`Sa.Configuration` assembles .NET configuration from the usual sources — `appsettings.json`, environment variables, command-line arguments, secret files and a database — and adds three things the standard `Microsoft.Extensions.Configuration` does not have:

- **secrets instead of hardcoding** — the config keeps `{{sa_pg_password}}` and the real value comes from a file, an environment variable or an argument;
- **key aliases** — a flat name (`auth_base_url`) and a nested path (`sa:scope:api:auth:base_url`) start meaning the same thing;
- **per-client overlays** — every client gets its own root with the same key names over the shared values, reachable as a keyed `IConfiguration`.

On top of that, the package reloads only the dynamic layer and parses command-line arguments. All of it plugs into the standard pipeline, so an ASP.NET app needs one line.

## Contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [How the configuration is built](#how-the-configuration-is-built)
- [Secrets](#secrets)
- [Key aliases](#key-aliases)
- [Clients: core and overlays](#clients-core-and-overlays)
- [Dynamic layer reload](#dynamic-layer-reload)
- [Command-line arguments](#command-line-arguments)
- [Alongside Sa.Configuration.PostgreSql](#alongside-saconfigurationpostgresql)
- [Full DI example](#full-di-example)
- [Details](#details)
- [License](#license)

## Installation

```bash
dotnet add package Sa.Configuration
```

## Quick start

### 1. Wire up the package in `Program.cs`

```csharp
using Sa.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Arguments + secrets from files/ENV/CLI. appsettings and environment variables are
// already added by the host — nothing is added twice.
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

## How the configuration is built

Sources fall into two layers:

| Layer | What it is | How it changes at runtime |
|-------|------------|---------------------------|
| **Static** | `appsettings.json`, `appsettings.{env}.json`, environment variables, arguments, `secrets*.txt` | file edit, `IConfigurationRoot.Reload()`, or a restart |
| **Dynamic** | PostgreSQL or any other `ISaDynamicConfigurationSource` | `ISaConfigurationReloader.ReloadDynamic[Async]()` — **only** this layer is re-read |

One rule: **a source added later overrides the earlier one**. The secrets/alias pass always runs after the sources, so it sees the assembled configuration.

```csharp
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseStandardSources();   // appsettings.json → appsettings.{env}.json → env → args
    sa.UseSecrets();           // {{…}} and key aliases

    // Optional: the dynamic layer overrides the static one.
    sa.UseDynamic(new DatabaseConfigurationSource(
        new PostgreSqlConfigurationOptions("SELECT key, value FROM setting"),
        PgConfigurationConnection.FromServices()));
});
```

`UseStandardSources()` is idempotent: ASP.NET Core and the generic host already add those sources, so the method does nothing. The other builder methods are in [Builder: methods and order](#builder-methods-and-order).

## Secrets

### Where secrets come from

Sources are checked in order; the first hit wins:

| # | Source | Example |
|---|--------|---------|
| 1 | Command-line arguments | `--sa_pg_password=...` |
| 2 | Environment variables | `SA_PG_PASSWORD=...` |
| 3 | `.env.{env}` / `.env` | `SA_SECRETS_DECRYPTION_KEY=...` |
| 4 | `secrets.{client}.{env}.pub` / `secrets.{client}.pub` | encrypted client secrets |
| 5 | `secrets.{env}.pub` / `secrets.pub` | encrypted defaults |
| 6 | `secrets.{env}.txt` | plain file for one environment |
| 7 | `secrets.txt` | plain base file |

In short: the command line overrides everything, environment variables override files, an environment file overrides the base file.

`{env}` is `DOTNET_ENVIRONMENT` → `ASPNETCORE_ENVIRONMENT` → `environment`; if none is set, `Production` is used. For a short name like `dev`, set `DOTNET_ENVIRONMENT=dev` — the file names follow it.

### Placeholders

A plain `{{key}}` requires the secret to exist. When a setting is optional, write `{{?key}}`:

```json
{
  "optional_feature": "{{?feature_flag}}"
}
```

If `feature_flag` is found in no store, the whole string becomes `null` — no exception. Moreover, inside the configuration pipeline (`AddSaConfiguration` / `AddSaPostSecretProcessing`) **any** missing secret yields `null` rather than an error; the strict mode applies only to a direct `PopulateSecrets` call.

On substitution the value is trimmed and stripped of quotes `'`, `"`, `` ` ``. Nested placeholders expand up to 3 levels; a cycle produces a clear `InvalidOperationException`.

### Encrypted secrets

`secrets*.pub` files can be committed: values are encrypted with AES-256-GCM while key names and comments stay readable. The decryption key comes from environment variables:

```bash
export SA_SECRETS_DECRYPTION_CLIENT=lsr
export SA_SECRETS_DECRYPTION_KEY=...        # or base64:<32-byte key>
# the same variables can go into .env (the .env itself is not committed)
```

```bash
./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets.lsr.dev.pub
./build-sh/do-secrets.sh decrypt secrets.lsr.dev.pub
./build-sh/do-secrets.sh check
```

If an encrypted file exists but the key is missing, the app **fails at startup** — there is no silent fallback to plaintext.

How the format works, which encoding pitfalls exist and how decryption behaves — in [SecretStore/Readme.md](SecretStore/Readme.md#encodings-and-value-format).

### Secrets outside `IConfiguration`

Sometimes secrets are needed outside configuration — in a console tool, for instance. Then `Secrets` is used directly:

```csharp
using Sa.Configuration.SecretStore;

// Default chain: CLI → ENV → .env → secrets*.pub → secrets.{env}.txt → secrets.txt
var secrets = Secrets.CreateDefault();

string? connection = secrets.PopulateSecrets("Server={{host}};Pwd={{sa_pg_password}}");
string? password   = secrets.GetSecret("sa_pg_password");
```

A custom chain is any `ISecretStore` (file, environment variables, arguments, in-memory) plus `AddStore(...)`; priority is LIFO, the store added last wins. The base file name and the environment are options:

```csharp
var secrets = new Secrets(new FileSecretStore("my-secrets.txt"));
secrets.AddStore(new InMemorySecretStore().AddSecret("override_key", "override_value"));

var custom = Secrets.CreateDefault(new SecretOptions
{
    FileName = "app-secrets.txt",
    EnvironmentName = "Staging",
});
```

Details and the built-in stores — in [SecretStore/Readme.md](SecretStore/Readme.md).

## Key aliases

One setting is sometimes named differently in different systems: PHP or the database uses the flat `auth_base_url`, while C# prefers the path `sa:scope:api:auth:base_url`. An alias links the two names without rewriting either the config or the code:

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

Left is the path C# reads, right is the name the value actually lives under. After the pass both keys return the same value, and the flat key does not disappear:

```csharp
app.Configuration["sa:scope:api:auth:base_url"]; // → "https://php.example.com"
app.Configuration["auth_base_url"];               // → "https://php.example.com"
```

Default behaviour:

- an empty or missing alias value does not touch the canonical path — the lower-priority source answers for it;
- a non-empty flat value is written into the path (`CanonicalValueWins = true` reverses that priority);
- the alias applies to the **assembled** configuration, so the flat key and the path may live in different sources;
- keys are compared case-insensitively, and `__` in the mapping is read as `:`.

| I want | Setting |
|--------|---------|
| Link a flat key to a nested path | `KeyAliases["path"] = "flat_key"` |
| The flat key wins | default |
| The nested path wins | `CanonicalValueWins = true` |
| No value | the path is untouched, the lower-priority source answers |

Registration order, `FinalizeSaConfiguration()`, custom transforms and edge cases — in [Aliases: full rules](#aliases-full-rules).

## Clients: core and overlays

The core holds the shared values. A client has **its own root** with **the same key names**, laid over the shared ones: reading takes the client value first and falls back to the core.

```text
core ("s3_url" = "A")                     ← shared value
client1 ("s3_url" = "B1")                 ← separate root, same key name
client2 ("s3_url" = "C")                  ← separate root, same key name

cf["s3_url"]                             → "A"
clients.GetClient("client1")["s3_url"]   → "B1"
clients.GetClient("client2")["s3_url"]   → "C"
```

There is no `sa:clients:{client}:{key}` prefix: the roots are independent, so one client's values cannot leak into the core or into another client. A client exists as soon as a provider has returned at least one row for it.

The canonical shape is two tables: shared values in `setting`, overrides in `client_settings` keyed by `client_id`.

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

A provider is a callback that returns flat rows `(client_id, key, value)` for **all** clients at once; the registry does the grouping. The callback's dependencies (up to three plus a `CancellationToken`) come from DI:

```csharp
using Sa.Configuration.Clients;

builder.Services.AddSingleton<IClientSettingsStore, PgClientSettingsStore>();

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IClientSettingsStore>(
        (store, cancellationToken) => store.LoadAllAsync(cancellationToken));
});
```

When the clients live in the static configuration, no database is needed:

```json
{
  "s3_url": "A",
  "theme": "dark",

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

Reading:

```csharp
public sealed class SettingsEndpoints(IConfiguration core, IClientConfigurations clients)
{
    public string CoreS3Url => core["s3_url"]!;                              // "A"
    public string Client1S3Url => clients.GetClient("client1")["s3_url"]!;   // "B1"
    public string Client2S3Url => clients.GetClient("client2")["s3_url"]!;   // "C"
    public IReadOnlyCollection<string> Known => clients.Clients;             // ["client1", "client2"]
}
```

An unknown client sees only the core; `TryGet` never throws, even with `ThrowForUnknownClient`. Providers are called lazily and again on `ReloadDynamicAsync()`, so a new client appears without a restart.

Overlay rules, keyed injection (`[FromKeyedServices("acme")] IConfiguration`), and attaching/removing clients at runtime — in [Clients: full rules](#clients-full-rules).

## Dynamic layer reload

`IConfigurationRoot.Reload()` re-reads **all** providers, files included. When only the database layer should be refreshed, use the runtime:

```csharp
ISaConfigurationReloader reloader = app.Services.GetRequiredService<ISaConfigurationReloader>();

await reloader.ReloadDynamicAsync(cancellationToken);
```

Dynamic providers are reloaded, the pass (placeholders, aliases, custom transforms) is recomputed from the fresh values, and reload tokens are raised. Static providers and their file watchers are untouched. A failed load keeps the previous snapshot: a temporary database problem never blanks a working configuration.

No background service is registered unless asked for:

```csharp
builder.Services.AddSaConfigurationRuntime(builder.Configuration);   // manual only
builder.Services.AddSaConfigurationRuntime(builder.Configuration, o =>
{
    o.InitialLoad = SaDynamicInitialLoad.Hosted;      // load before the server starts
    o.DynamicReload = true;                           // periodic polling
    o.DynamicReloadInterval = TimeSpan.FromSeconds(15);
});
```

All options and the late-binding (`FromServices`) behaviour — in [Dynamic reload: options](#dynamic-reload-options).

## Command-line arguments

```csharp
using Sa.Configuration.CommandLine;

// some.exe --config_db /share/data.db --debug --port -5
var args = new Arguments(args);

string? configDb  = args["config_db"];       // → "/share/data.db"
bool?   debug     = args.GetBool("debug");   // → true
int?    port      = args.GetInt("port");     // → -5
TimeSpan? timeout = args.GetTimeSpan("timeout");
```

Supported: `--key value`, `--key=value`, `-key value`, `-key=value` and the boolean `-flag`; negative numbers are treated as values. A double underscore in a name is how you spell `:`: `--sa__pg__host` is read as `sa:pg:host`.

Full format coverage, typed getters and edge cases — in [CommandLine/Readme.md](CommandLine/Readme.md).

## Alongside Sa.Configuration.PostgreSql

The PostgreSQL package adds a dynamic source and places the secrets pass after it:

```csharp
using Sa.Configuration.PostgreSql;

// The app pool (or an NpgsqlDataSource): nothing extra is created.
builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));

// Or an explicit pool, or a connection string owned by the source itself:
builder.Configuration.AddSaPostgreSqlConfiguration(
    appDataSource, new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
builder.Configuration.AddSaPostgreSqlConfiguration(
    "Host=db;Database=app;Username=app;Password=…",
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
```

The database layer is registered after the static sources, so it overrides them; aliases apply on top of the database values. Query shapes and reload details — in [the package README](../Sa.Configuration.PostgreSql/Readme.md).

## Full DI example

Static config + core dynamic source + two clients, one connection pool, and no background service unless asked:

```csharp
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Static: the host already added appsettings/env/args, so UseStandardSources does nothing here.
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

// Every client gets its own root; keys are stored as-is, without a prefix. The provider takes
// the store from DI and returns `(client_id, key, value)` rows for all clients at once.
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

The provider behind `clients.Add<IClientSettingsStore>(...)` — one query, flat rows, the registry does the grouping:

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

Sample data — the core table and the client table:

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
    ('acme', 's3_url',   'B1'),   -- acme overrides only s3_url
    ('acme', 'theme',    'light'),
    ('beta', 's3_url',   'C'),    -- beta has its own value
    ('beta', 'language', 'de');
```

`GET /settings` → `s3_url` = `A`, `theme` = `dark`, `language` = `en`;
`GET /settings/acme` → `B1` / `light` / `en`;
`GET /settings/beta` → `C` / `dark` / `de`;
`GET /settings/unknown` → `A` / `dark` / `en`.

## Details

The sections below are reference material: the full rules and every option. The main text covers them briefly and links here.

### Aliases: full rules

**Several registrations.** `AddSaConfiguration` can be called more than once — the calls accumulate:

```csharp
builder.Configuration
    .AddSaConfiguration(new SecretOptions { KeyAliases = { ["sa:…:base_url"] = "auth_base_url" } })
    .AddSaConfiguration(new SecretOptions { KeyAliases = { ["sa:…:token_ttl"] = "auth_token_ttl" } });
```

- each call **appends** its `KeyAliases` to one shared map; if a canonical key is declared twice, the **last** call wins;
- secret options (`FileName`, `LoadDotEnv`, `Encryption`, `Args`) also come from the **last** call;
- exactly **one** pass and **one** command-line source are registered, so secrets are read once — on the first `Build()` (registration itself does not touch files);
- `CanonicalValueWins` is one flag for the whole map, also taken from the last call.

**Order.** The pass writes derived values and therefore overrides every source registered **before** it. A source added **after** the last `AddSaConfiguration` is a normal provider: for its keys it overrides the alias. When sources are registered later, call `FinalizeSaConfiguration()` as the last step:

```csharp
builder.Configuration
    .AddSaPostgreSqlConfiguration(...)   // a source with canonical keys
    .AddSaConfiguration(...)             // aliases
    .FinalizeSaConfiguration();          // the pass moves to the end
```

**Custom transforms.** `AddSaConfigurationTransform` adds a step that receives the assembled configuration and returns **only** the keys it changes. Transforms run after the built-in ones (placeholders, then aliases) in registration order, and each sees the previous results:

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

**Where the value is looked up.** The alias applies to the assembled configuration built from **all** sources, so the flat key and the canonical path may live in different places: `auth_base_url` in PostgreSQL, `sa:scope:api:auth:base_url` in `appsettings.json`. The alias joins them.

**Who overrides whom.**

- by default a non-empty flat value is written into the canonical path;
- with `CanonicalValueWins = true` the canonical value has priority, and the alias fills the path only when it is empty:

```csharp
builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        CanonicalValueWins = true,
    },
});
```

- when the alias is empty or missing, the canonical path is untouched: it keeps its value or stays missing, and the lower-priority source answers for it.

**Small print.**

- case does not matter: `Auth_Base_Url` and `auth_base_url` are the same;
- `__` = `:`: `sa__scope__api__auth__base_url` is equivalent to `sa:scope:api:auth:base_url`;
- an empty key or empty string in the mapping is a startup error (such an alias could never match);
- an empty mapping changes nothing;
- `Reload()` recomputes the pass: a key that disappeared from a source or from the mapping stops being served.

### Clients: full rules

**Providers.** The `clients.Add<T1[,T2[,T3]]>(...)` callback receives one to three services from the container plus a `CancellationToken` and returns `(client_id, key, value)` rows for all clients at once. The provider knows nothing about roots, splitters or prefixes.

| Shape | When to use |
|---|---|
| `clients.Add<T1>((t1, ct) => rows)` | the provider needs one DI service |
| `clients.Add<T1, T2>((t1, t2, ct) => rows)` | two services (say, a store and a logger) |
| `clients.Add<T1, T2, T3>((t1, t2, t3, ct) => rows)` | three services |
| `clients.UseConfiguration(configuration)` | clients and values live in a configuration section, no database needed |
| `AttachAsync(client, source)` | a client appears at runtime |
| `clients.Configure(o => …)` | tune `ThrowForUnknownClient` / `EagerLoad` |

Several providers can be registered — their rows are merged per client, and on a key collision the later row wins:

```csharp
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IClientSettingsStore>((store, ct) => store.LoadAllAsync(ct));

    clients.Add<IDefaultsStore, ILogger<Clients>>(
        (defaults, logger, ct) => defaults.LoadAsync(logger, ct));
});
```

**Clients from a section.** `UseConfiguration` registers the same kind of provider over a section: child elements are client ids, keys inside are named like the core ones and are read **without** the section prefix. A different layout changes only the path:

```csharp
clients.UseConfiguration(builder.Configuration, new ConfigurationClientSourceOptions
{
    SectionPath = "tenants",        // tenants:acme:s3_url, tenants:beta:s3_url, …
});
```

**Reading rules.**

- a client value beats the core value for the same key; when the client has none, the core value is used;
- a client sees only its own root plus the core — never another client's values; the clients section of the section provider is hidden from every view;
- an empty string is dropped when rows are read and falls back to the core value — `ClientConfigurationOptions.SkipEmptyValues` (on by default); `false` makes an empty string an explicit client value; an empty value produced by a transform stays local to the client;
- an unknown client reads core values; `TryGet` reports whether it is known and never throws, even with `ThrowForUnknownClient` (only `GetClient` throws);
- `Clients` is the union of ids returned by providers and clients attached at runtime;
- client rows go through the same pass as the core: placeholders are substituted and aliases expand inside the client's own rows (the alias source is looked up in the client first, then in the core). A derived value that merely repeats the core is not copied into the overlay — the client reads the core live; an unresolved placeholder reads as an empty string on the client. The exception is the section provider: the core pass nulls an unresolved placeholder **before** the section is read, so the client takes the core value;
- keys use the canonical `:` form: `sa__pg__host` is the same setting as `sa:pg:host`.

Sections work over both roots:

```csharp
IConfiguration db = clients.GetClient("client1").GetSection("db");
db["host"]; // the client1 value if present, otherwise the core value
```

**Aliases and placeholders inside a client overlay** work like the core:

```text
core:    auth_base_url = https://core.example.com   → sa:scope:api:auth:base_url = https://core.example.com
client1: auth_base_url = https://acme.example.com   → sa:scope:api:auth:base_url = https://acme.example.com
client2: (no such row)                              → https://core.example.com, read live from the core

client1: api_token = {{acme_token}}                 → the substituted secret, never a literal
```

**Keyed injection.** When the client id is known at compile time, its root is resolved by key:

```csharp
using Microsoft.Extensions.DependencyInjection;   // [FromKeyedServices]

public static class Clients
{
    public const string Acme = "acme";
    public const string Beta = "beta";
}

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
    clients.UseConfiguration(builder.Configuration));

// Minimal API: the known client comes from the container.
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

// Code that has an IServiceProvider:
IConfiguration beta = app.Services.GetRequiredKeyedService<IConfiguration>(Clients.Beta);
beta["s3_url"];                    // "C"
```

The key is the client id from `client_settings.client_id` (or a child key of the section), compared case-insensitively. Injection happens on every resolve, so a client added or updated later is visible on the next request without re-registration. The keyed registration uses `KeyedService.AnyKey`, so a name known only at runtime works too. Enumerating keyed services does not enumerate clients — use `IClientConfigurations.Clients` for that.

A name known only at runtime is passed to the registry:

```csharp
app.MapGet("/settings/{client}", (string client, IClientConfigurations clients) =>
    new { Client = client, S3Url = clients.GetClient(client)["s3_url"] });
```

**Attaching and removing at runtime.**

```csharp
await clients.AttachAsync("acme", new MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?> { ["s3_url"] = "B1" },
});

await clients.RemoveAsync("acme");   // the view falls back to core values again
```

`AttachAsync` replaces an existing overlay for the same client and disposes the replaced one. `RemoveAsync` returns `false` and changes nothing for a client a provider still returns: its overlay would be rebuilt on the next access anyway. Only an attachment added at runtime can be removed.

Views returned by `GetClient` are live: on each access they ask the registry for the current root, so attach/remove/reload is visible through a reference obtained earlier. `GetReloadToken()` on such a view composes the core, the overlay and the registry, so the callback fires on attach/remove as well. The registry is thread-safe.

### Builder: methods and order

| Method | What it adds |
|--------|--------------|
| `UseStandardSources(...)` | `appsettings.json`, `appsettings.{env}.json`, environment variables, arguments. **Idempotent**: in ASP.NET Core / the generic host the host already added them, so the method does nothing |
| `UseSecrets(...)` | the existing secrets pipeline: placeholders, aliases, the arguments source |
| `UseDynamic(source)` | a source whose values change at runtime; placed after the static ones |
| `Use(source)` | any `IConfigurationSource` — for sources without a dedicated method |
| `Finalize()` | moves the pass to the end; needed only when sources are added directly to `sa.Builder` |

A source registered after the last Sa call is a normal provider and overrides the pass for its keys. `Finalize()` (or `sa.Builder.Add(...)`, then `Finalize()`) is the explicit way to restore the order.

### Dynamic reload: options

| Option | Default | Meaning |
|--------|---------|---------|
| `InitialLoad` | `Explicit` | `Explicit` — nothing happens until you call `EnsureInitializedAsync`; `Hosted` — a one-shot hook loads before the first request and fails the startup on error; `Background` — the host starts immediately and the error is logged |
| `DynamicReload` | `false` | registers exactly one hosted service with a `PeriodicTimer`; without it there is no timer |
| `DynamicReloadInterval` | 30 s | polling period |
| `DynamicReloadInitialDelay` | `null` | delay before the first poll (one interval by default) |

Late binding (`PgConfigurationConnection.FromServices()`) resolves from the container during `EnsureInitializedAsync`, i.e. after `builder.Build()`; with an explicit `DataSource`/`ConnectionString` the init step is not needed. Initialization is idempotent; a failed periodic reload is just logged, and the previous snapshot stays in force.

### Encodings and value format

Encodings, the `ENC1:` format, payload length, Unicode behaviour and `.env` rules are described in [SecretStore/Readme.md](SecretStore/Readme.md#encodings-and-value-format).

## License

MIT
