# Sa.Configuration

**Русский** | [English](Readme.md)

`Sa.Configuration` собирает конфигурацию .NET из обычных источников — `appsettings.json`, переменных окружения, аргументов командной строки, файлов секретов и базы — и добавляет три вещи, которых нет в стандартном `Microsoft.Extensions.Configuration`:

- **секреты вместо хардкода** — в конфиге остаётся `{{sa_pg_password}}`, а реальное значение приходит из файла, переменной окружения или аргумента;
- **алиасы ключей** — плоское имя (`auth_base_url`) и вложенный путь (`sa:scope:api:auth:base_url`) начинают означать одно и то же;
- **клиентские оверлеи** — у каждого клиента свой корень с теми же именами ключей поверх общих значений, доступный как keyed `IConfiguration`.

Дополнительно пакет перезагружает только динамический слой и разбирает аргументы командной строки. Всё это работает поверх стандартного конвейера, поэтому в ASP.NET подключается одной строкой.

## Содержание

- [Установка](#установка)
- [Быстрый старт](#быстрый-старт)
- [Как устроена конфигурация](#как-устроена-конфигурация)
- [Секреты](#секреты)
- [Алиасы ключей](#алиасы-ключей)
- [Клиенты: ядро и оверлеи](#клиенты-ядро-и-оверлеи)
- [Обновление динамического слоя](#обновление-динамического-слоя)
- [Аргументы командной строки](#аргументы-командной-строки)
- [Вместе с Sa.Configuration.PostgreSql](#вместе-с-saconfigurationpostgresql)
- [Полный пример DI](#полный-пример-di)
- [Подробности](#подробности)
- [Лицензия](#лицензия)

## Установка

```bash
dotnet add package Sa.Configuration
```

## Быстрый старт

### 1. Подключаем пакет в `Program.cs`

```csharp
using Sa.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Аргументы + секреты из файлов/ENV/CLI. appsettings и переменные окружения хост
// уже добавил сам — повторно их никто не добавляет.
builder.Configuration.AddSaConfiguration();

var app = builder.Build();
```

### 2. Создаём файл секретов `secrets.txt`

```ini
# Postgres
sa_pg_host=localhost
sa_pg_user=postgres
sa_pg_port=5432
sa_pg_database=myapp
sa_pg_schema=public
sa_pg_password=superSecret123

# API ключи
api_key=abc123xyz
jwt_secret=h8k2m9p0
```

> ⚠️ Не забудьте добавить `secrets*.txt` в `.gitignore`!

### 3. Пишем плейсхолдеры в `appsettings.json`

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

### 4. Читаем конфигурацию как обычно

```csharp
var pgConn = app.Configuration["sa:pg:connection"];
// → "User ID=postgres;Password=superSecret123;Host=localhost;..."
```

## Как устроена конфигурация

Источники делятся на два слоя:

| Слой | Что это | Как обновляется в рантайме |
|------|---------|----------------------------|
| **Static** | `appsettings.json`, `appsettings.{env}.json`, переменные окружения, аргументы, `secrets*.txt` | правка файла, `IConfigurationRoot.Reload()` или рестарт |
| **Dynamic** | PostgreSQL и любой другой `ISaDynamicConfigurationSource` | `ISaConfigurationReloader.ReloadDynamic[Async]()` — перечитывается **только** этот слой |

Правило одно: **источник, добавленный позже, перекрывает предыдущий**. Проход секретов и алиасов всегда выполняется после источников, поэтому видит уже собранную конфигурацию.

```csharp
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseStandardSources();   // appsettings.json → appsettings.{env}.json → env → args
    sa.UseSecrets();           // {{…}} и алиасы ключей

    // Необязательно: динамический слой перекрывает статику.
    sa.UseDynamic(new DatabaseConfigurationSource(
        new PostgreSqlConfigurationOptions("SELECT key, value FROM setting"),
        PgConfigurationConnection.FromServices()));
});
```

`UseStandardSources()` идемпотентен: в ASP.NET Core и generic host эти источники уже добавлены, и метод ничего не делает. Остальные методы builder'а — в разделе [Builder: методы и порядок](#builder-методы-и-порядок).

## Секреты

### Откуда берутся секреты

Источники проверяются по порядку, побеждает первый найденный:

| # | Источник | Пример |
|---|----------|--------|
| 1 | Аргументы командной строки | `--sa_pg_password=...` |
| 2 | Переменные окружения | `SA_PG_PASSWORD=...` |
| 3 | `.env.{env}` / `.env` | `SA_SECRETS_DECRYPTION_KEY=...` |
| 4 | `secrets.{client}.{env}.pub` / `secrets.{client}.pub` | зашифрованные секреты клиента |
| 5 | `secrets.{env}.pub` / `secrets.pub` | зашифрованные значения по умолчанию |
| 6 | `secrets.{env}.txt` | обычный файл для конкретной среды |
| 7 | `secrets.txt` | обычный базовый файл |

Коротко: командная строка перекрывает всё, переменные окружения — файлы, файл среды — базовый файл.

`{env}` — это `DOTNET_ENVIRONMENT` → `ASPNETCORE_ENVIRONMENT` → `environment`; если нигде не задано, используется `Production`. Для короткого имени вроде `dev` задайте `DOTNET_ENVIRONMENT=dev` — имена файлов последуют за ним.

### Плейсхолдеры

Обычный `{{key}}` требует, чтобы секрет существовал. Если настройка нужна «если есть», пишите `{{?key}}`:

```json
{
  "optional_feature": "{{?feature_flag}}"
}
```

Если `feature_flag` не найден ни в одном хранилище, вся строка становится `null`, исключения нет. Более того, в цепочке конфигурации (`AddSaConfiguration` / `AddSaPostSecretProcessing`) **любой** отсутствующий секрет даёт `null`, а не ошибку; строгий режим — только при прямом вызове `PopulateSecrets`.

Значение при подстановке обрезается по краям и освобождается от кавычек `'`, `"`, `` ` ``. Глубина вложенных плейсхолдеров — до 3 уровней, цикл даёт понятный `InvalidOperationException`.

### Зашифрованные секреты

Файлы `secrets*.pub` можно коммитить: значения зашифрованы AES-256-GCM, имена ключей и комментарии остаются читаемыми. Ключ расшифровки задаётся переменными окружения:

```bash
export SA_SECRETS_DECRYPTION_CLIENT=lsr
export SA_SECRETS_DECRYPTION_KEY=...        # или base64:<32-байтный ключ>
# те же переменные можно положить в .env (сам .env в репозиторий не коммитится)
```

```bash
./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets.lsr.dev.pub
./build-sh/do-secrets.sh decrypt secrets.lsr.dev.pub
./build-sh/do-secrets.sh check
```

Если зашифрованный файл есть, а ключа нет, приложение **падает на старте** — молчаливого отката на plaintext не будет.

Как устроен формат, какие бывают проблемы с кодировками и как ведёт себя расшифровка — в [SecretStore/Readme-ru.md](SecretStore/Readme-ru.md#кодировки-и-формат-значений).

### Секреты без `IConfiguration`

Иногда секреты нужны вне конфигурации — например, в консольной утилите. Тогда `Secrets` используется напрямую:

```csharp
using Sa.Configuration.SecretStore;

// Цепочка по умолчанию: CLI → ENV → .env → secrets*.pub → secrets.{env}.txt → secrets.txt
var secrets = Secrets.CreateDefault();

string? connection = secrets.PopulateSecrets("Server={{host}};Pwd={{sa_pg_password}}");
string? password   = secrets.GetSecret("sa_pg_password");
```

Своя цепочка — это любое хранилище `ISecretStore` (файл, переменные окружения, аргументы, in-memory) плюс `AddStore(...)`; приоритет LIFO, побеждает добавленное последним. Имя базового файла и среду можно задать опциями:

```csharp
var secrets = new Secrets(new FileSecretStore("my-secrets.txt"));
secrets.AddStore(new InMemorySecretStore().AddSecret("override_key", "override_value"));

var custom = Secrets.CreateDefault(new SecretOptions
{
    FileName = "app-secrets.txt",
    EnvironmentName = "Staging",
});
```

Подробности и список встроенных хранилищ — в [SecretStore/Readme-ru.md](SecretStore/Readme-ru.md).

## Алиасы ключей

Иногда одна настройка называется по-разному в разных системах: в PHP или в базе это плоский `auth_base_url`, а в C# удобнее путь `sa:scope:api:auth:base_url`. Алиас связывает два имени, не заставляя переписывать ни конфиг, ни код:

```csharp
using Sa.Configuration;

builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases =
        {
            ["sa:scope:api:auth:base_url"] = "auth_base_url",   // путь → плоский ключ
            ["sa:scope:api:auth:token_ttl"] = "auth_token_ttl",
        },
    },
});
```

Слева — путь, который читает C#, справа — имя, под которым значение реально лежит. После прохода оба ключа отдают одно значение, а плоский ключ никуда не исчезает:

```csharp
app.Configuration["sa:scope:api:auth:base_url"]; // → "https://php.example.com"
app.Configuration["auth_base_url"];               // → "https://php.example.com"
```

Поведение по умолчанию:

- пустое или отсутствующее значение алиаса не трогает канонический путь — за него отвечает источник с меньшим приоритетом;
- непустое значение плоского ключа записывается в путь (`CanonicalValueWins = true` меняет приоритет на обратный);
- алиас применяется к **итоговой** конфигурации, поэтому плоский ключ и путь могут лежать в разных источниках;
- ключи сравниваются без учёта регистра, а `__` в маппинге читается как `:`.

| Хочу | Настройка |
|------|-----------|
| Связать плоский ключ с вложенным путём | `KeyAliases["путь"] = "плоский_ключ"` |
| Плоский ключ побеждает | по умолчанию |
| Вложенный путь побеждает | `CanonicalValueWins = true` |
| Значения нет | путь не трогается, работает lower-priority источник |

Порядок регистрации, `FinalizeSaConfiguration()`, свои трансформы и краевые случаи — в разделе [Алиасы: полные правила](#алиасы-полные-правила).

## Клиенты: ядро и оверлеи

Ядро хранит общие значения. У клиента **свой корень** с **теми же именами ключей**, положенный поверх общих: чтение сначала берёт значение клиента, а если его нет — значение ядра.

```text
ядро ("s3_url" = "A")                     ← общее значение
client1 ("s3_url" = "B1")                 ← отдельный корень, то же имя ключа
client2 ("s3_url" = "C")                  ← отдельный корень, то же имя ключа

cf["s3_url"]                             → "A"
clients.GetClient("client1")["s3_url"]   → "B1"
clients.GetClient("client2")["s3_url"]   → "C"
```

Префикса `sa:clients:{client}:{key}` нет: корни независимы, поэтому значения одного клиента не могут протечь ни в ядро, ни в другого клиента. Клиент существует с того момента, как провайдер вернул для него хотя бы одну строку.

Каноническая схема — две таблицы: общие значения в `setting`, переопределения в `client_settings` по `client_id`.

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

Провайдер — это колбэк, который отдаёт плоские строки `(client_id, key, value)` сразу по всем клиентам; группировкой занимается реестр. Зависимости колбэка (до трёх плюс `CancellationToken`) приходят из DI:

```csharp
using Sa.Configuration.Clients;

builder.Services.AddSingleton<IClientSettingsStore, PgClientSettingsStore>();

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IClientSettingsStore>(
        (store, cancellationToken) => store.LoadAllAsync(cancellationToken));
});
```

Если клиенты лежат в статической конфигурации, база не нужна:

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

Чтение:

```csharp
public sealed class SettingsEndpoints(IConfiguration core, IClientConfigurations clients)
{
    public string CoreS3Url => core["s3_url"]!;                              // "A"
    public string Client1S3Url => clients.GetClient("client1")["s3_url"]!;   // "B1"
    public string Client2S3Url => clients.GetClient("client2")["s3_url"]!;   // "C"
    public IReadOnlyCollection<string> Known => clients.Clients;             // ["client1", "client2"]
}
```

Неизвестный клиент видит только ядро; `TryGet` не бросает исключений даже при `ThrowForUnknownClient`. Провайдеры вызываются лениво и повторно при `ReloadDynamicAsync()`, поэтому новый клиент появляется без рестарта.

Правила наложения, keyed-инъекция (`[FromKeyedServices("acme")] IConfiguration`), подключение и удаление клиентов в рантайме — в разделе [Клиенты: полные правила](#клиенты-полные-правила).

## Обновление динамического слоя

`IConfigurationRoot.Reload()` перечитывает **все** провайдеры, включая файлы. Когда нужно обновить только слой БД, используйте runtime:

```csharp
ISaConfigurationReloader reloader = app.Services.GetRequiredService<ISaConfigurationReloader>();

await reloader.ReloadDynamicAsync(cancellationToken);
```

Перезагружаются динамические провайдеры, затем проход (плейсхолдеры, алиасы, свои трансформы) пересчитывается от свежих значений, затем поднимаются reload-токены. Статические провайдеры и их file watcher'ы не трогаются. Неудачная загрузка оставляет предыдущий снимок: временная проблема с БД не обнулит работающую конфигурацию.

Фоновая служба не регистрируется, пока её не попросят:

```csharp
builder.Services.AddSaConfigurationRuntime(builder.Configuration);   // только вручную
builder.Services.AddSaConfigurationRuntime(builder.Configuration, o =>
{
    o.InitialLoad = SaDynamicInitialLoad.Hosted;      // загрузка до старта сервера
    o.DynamicReload = true;                           // периодический опрос
    o.DynamicReloadInterval = TimeSpan.FromSeconds(15);
});
```

Все опции и поведение при позднем связывании (`FromServices`) — в разделе [Обновление динамики: опции](#обновление-динамики-опции).

## Аргументы командной строки

```csharp
using Sa.Configuration.CommandLine;

// some.exe --config_db /share/data.db --debug --port -5
var args = new Arguments(args);

string? configDb  = args["config_db"];       // → "/share/data.db"
bool?   debug     = args.GetBool("debug");   // → true
int?    port      = args.GetInt("port");     // → -5
TimeSpan? timeout = args.GetTimeSpan("timeout");
```

Поддерживаются `--key value`, `--key=value`, `-key value`, `-key=value` и булев `-flag`; отрицательные числа считаются значениями. Двойное подчёркивание в имени — способ записать `:`: `--sa__pg__host` читается как `sa:pg:host`.

Полный разбор форматов, типизированные геттеры и краевые случаи — в [CommandLine/Readme-ru.md](CommandLine/Readme-ru.md).

## Вместе с Sa.Configuration.PostgreSql

Пакет PostgreSQL добавляет динамический источник и сам ставит проход секретов после него:

```csharp
using Sa.Configuration.PostgreSql;

// Пул приложения (или NpgsqlDataSource): ничего лишнего не создаётся.
builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));

// Либо явный пул, либо строка подключения, которой владеет сам источник:
builder.Configuration.AddSaPostgreSqlConfiguration(
    appDataSource, new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
builder.Configuration.AddSaPostgreSqlConfiguration(
    "Host=db;Database=app;Username=app;Password=…",
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
```

Слой БД зарегистрирован позже статики, поэтому перекрывает её; алиасы применяются поверх значений из БД. Формы запросов и детали обновления — в [README пакета](../Sa.Configuration.PostgreSql/Readme-ru.md).

## Полный пример DI

Статика + динамика ядра + два клиента, один пул соединений и никакой фоновой службы без явной просьбы:

```csharp
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Статика: хост уже добавил appsettings/env/args, поэтому UseStandardSources здесь ничего не делает.
// Динамический источник читает общие значения через пул, зарегистрированный ниже.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets();                                   // {{placeholder}} + алиасы ключей
    sa.UseDynamic(new DatabaseConfigurationSource(
        new PostgreSqlConfigurationOptions("SELECT key, value FROM setting"),
        PgConfigurationConnection.FromServices("orders")));
});

builder.Services.AddSaPostgreSqlDataSource("orders", b => b.FromConfiguration("Postgres"));
builder.Services.AddSingleton<IClientSettingsStore, PgClientSettingsStore>();

// Каждый клиент получает свой корень; ключи хранятся как есть, без префикса. Провайдер достаёт
// хранилище из DI и возвращает строки `(client_id, key, value)` сразу по всем клиентам.
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IClientSettingsStore>((store, ct) => store.LoadAllAsync(ct));
});

builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.InitialLoad = SaDynamicInitialLoad.Hosted;   // до первого запроса
});

var app = builder.Build();

app.MapGet("/settings", (IConfiguration configuration, IClientConfigurations clients) => new
{
    Theme = configuration["theme"],                     // значение ядра
    S3Url = configuration["s3_url"],                    // значение ядра
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

Провайдер за `clients.Add<IClientSettingsStore>(...)` — один запрос, плоские строки, группировку делает реестр:

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

Пример данных — таблица ядра и таблица клиентов:

```sql
CREATE TABLE setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);

INSERT INTO setting (key, value) VALUES
    ('s3_url',   'A'),      -- значение ядра для всех клиентов
    ('theme',    'dark'),
    ('language', 'en');

CREATE TABLE client_settings (
    client_id TEXT NOT NULL,
    key       TEXT NOT NULL,
    value     TEXT NOT NULL,
    PRIMARY KEY (client_id, key)
);

INSERT INTO client_settings (client_id, key, value) VALUES
    ('acme', 's3_url',   'B1'),   -- acme перекрывает только s3_url
    ('acme', 'theme',    'light'),
    ('beta', 's3_url',   'C'),    -- у beta своё значение
    ('beta', 'language', 'de');
```

`GET /settings` → `s3_url` = `A`, `theme` = `dark`, `language` = `en`;
`GET /settings/acme` → `B1` / `light` / `en`;
`GET /settings/beta` → `C` / `dark` / `de`;
`GET /settings/unknown` → `A` / `dark` / `en`.

## Подробности

Разделы ниже — справочник: полные правила и все опции. В основном тексте о них сказано коротко, со ссылками сюда.

### Алиасы: полные правила

**Несколько регистраций.** Вызывать `AddSaConfiguration` можно несколько раз — вызовы складываются:

```csharp
builder.Configuration
    .AddSaConfiguration(new SecretOptions { KeyAliases = { ["sa:…:base_url"] = "auth_base_url" } })
    .AddSaConfiguration(new SecretOptions { KeyAliases = { ["sa:…:token_ttl"] = "auth_token_ttl" } });
```

- каждый вызов **дописывает** свои `KeyAliases` в одну общую карту; если канонический ключ объявлен дважды, побеждает **последний** вызов;
- опции секретов (`FileName`, `LoadDotEnv`, `Encryption`, `Args`) тоже берутся из **последнего** вызова;
- регистрируется **один** проход и **один** источник командной строки, поэтому секреты читаются один раз — при первом `Build()` (сама регистрация файлы не трогает);
- `CanonicalValueWins` — один флаг на всю карту, его значение тоже берётся из последнего вызова.

**Порядок.** Проход записывает производные значения и поэтому побеждает все источники, зарегистрированные **до** него. Источник, добавленный **после** последнего `AddSaConfiguration`, — обычный провайдер: для своих ключей он перекрывает алиас. Если источники регистрируются позже, последним шагом вызывайте `FinalizeSaConfiguration()`:

```csharp
builder.Configuration
    .AddSaPostgreSqlConfiguration(...)   // источник с каноническими ключами
    .AddSaConfiguration(...)             // алиасы
    .FinalizeSaConfiguration();          // проход уходит в конец
```

**Свои трансформы.** `AddSaConfigurationTransform` добавляет шаг, который получает суммарную конфигурацию и возвращает **только** те ключи, которые меняет. Трансформы выполняются после встроенных (плейсхолдеры, затем алиасы) в порядке регистрации, и каждая видит результат предыдущих:

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

**Где ищется значение.** Алиас применяется к итоговой конфигурации, собранной из **всех** источников, поэтому плоский ключ и канонический путь могут лежать в разных местах: `auth_base_url` — в PostgreSQL, `sa:scope:api:auth:base_url` — в `appsettings.json`. Алиас их склеит.

**Кто кого перекрывает.**

- по умолчанию непустое значение из плоского ключа записывается в канонический путь;
- с `CanonicalValueWins = true` каноническое значение имеет приоритет, и алиас заполняет путь только если тот пуст:

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

- если алиас пуст или отсутствует, канонический путь не трогается: он сохраняет своё значение или остаётся отсутствующим, и тогда за него отвечает источник с меньшим приоритетом.

**Мелочи.**

- регистр не важен: `Auth_Base_Url` и `auth_base_url` — одно и то же;
- `__` = `:`: `sa__scope__api__auth__base_url` эквивалентно `sa:scope:api:auth:base_url`;
- пустой ключ или пустая строка в маппинге — ошибка при старте (такой алиас никогда ни с чем не совпадёт);
- пустой маппинг ничего не меняет;
- `Reload()` пересчитывает проход: ключ, исчезнувший из источника или маппинга, перестаёт отдаваться.

### Клиенты: полные правила

**Провайдеры.** Колбэк `clients.Add<T1[,T2[,T3]]>(...)` получает от одного до трёх сервисов из контейнера плюс `CancellationToken` и возвращает строки `(client_id, key, value)` сразу по всем клиентам. Провайдер ничего не знает про корни, сплиттеры и префиксы.

| Форма | Когда использовать |
|---|---|
| `clients.Add<T1>((t1, ct) => rows)` | провайдеру нужен один сервис DI |
| `clients.Add<T1, T2>((t1, t2, ct) => rows)` | два сервиса (например, хранилище и логгер) |
| `clients.Add<T1, T2, T3>((t1, t2, t3, ct) => rows)` | три сервиса |
| `clients.UseConfiguration(configuration)` | клиенты и значения в секции конфигурации, база не нужна |
| `AttachAsync(client, source)` | клиент появляется в рантайме |
| `clients.Configure(o => …)` | настроить `ThrowForUnknownClient` / `EagerLoad` |

Провайдеров можно зарегистрировать несколько — строки объединяются по клиенту, при совпадении ключа побеждает пришедшая позже:

```csharp
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IClientSettingsStore>((store, ct) => store.LoadAllAsync(ct));

    clients.Add<IDefaultsStore, ILogger<Clients>>(
        (defaults, logger, ct) => defaults.LoadAsync(logger, ct));
});
```

**Клиенты из секции.** `UseConfiguration` регистрирует такой же провайдер поверх секции: дочерние элементы — id клиентов, ключи внутри называются так же, как в ядре, и читаются **без** префикса секции. Другая раскладка — меняется только путь:

```csharp
clients.UseConfiguration(builder.Configuration, new ConfigurationClientSourceOptions
{
    SectionPath = "tenants",        // tenants:acme:s3_url, tenants:beta:s3_url, …
});
```

**Правила чтения.**

- значение клиента выигрывает у ядра по тому же ключу; если у клиента значения нет, берётся значение ядра;
- клиент видит только свой корень плюс ядро — никогда значения другого клиента; секция клиентов у секционного провайдера скрыта из каждого представления;
- пустая строка отбрасывается при чтении строк и делегирует значение ядра — `ClientConfigurationOptions.SkipEmptyValues` (по умолчанию включён); `false` делает пустую строку явным собственным значением; пустое значение, полученное трансформацией, остаётся локальным у клиента;
- неизвестный клиент читает значения ядра; `TryGet` сообщает, известен ли он, и не бросает исключений даже при `ThrowForUnknownClient` (бросает только `GetClient`);
- `Clients` — объединение id из провайдеров и клиентов, добавленных в рантайме;
- строки клиента проходят тот же проход, что и ядро: плейсхолдеры подставляются, алиасы раскрываются внутри собственных строк клиента (источник алиаса ищется сначала у клиента, потом в ядре). Производное значение, которое лишь повторяет ядро, в оверлей не копируется — клиент читает ядро «живьём»; нераскрытый плейсхолдер читается у клиента как пустая строка. Исключение — секционный провайдер: проход ядра обнуляет нераскрытый плейсхолдер до чтения секции, и клиент берёт значение ядра;
- ключи используют каноническую запись с `:`: `sa__pg__host` — та же настройка, что `sa:pg:host`.

Секции работают поверх обоих корней:

```csharp
IConfiguration db = clients.GetClient("client1").GetSection("db");
db["host"]; // значение client1, если оно есть, иначе значение ядра
```

**Алиасы и плейсхолдеры внутри оверлея** клиента работают как в ядре:

```text
ядро:    auth_base_url = https://core.example.com   → sa:scope:api:auth:base_url = https://core.example.com
client1: auth_base_url = https://acme.example.com   → sa:scope:api:auth:base_url = https://acme.example.com
client2: (такой строки нет)                         → https://core.example.com, читается «живьём» из ядра

client1: api_token = {{acme_token}}                 → подставленный секрет, никогда литерал
```

**Keyed-инъекция.** Когда id клиента известен на этапе компиляции, его корень достаётся по ключу:

```csharp
using Microsoft.Extensions.DependencyInjection;   // [FromKeyedServices]

public static class Clients
{
    public const string Acme = "acme";
    public const string Beta = "beta";
}

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
    clients.UseConfiguration(builder.Configuration));

// Minimal API: известный клиент приходит из контейнера.
app.MapGet("/acme/settings", ([FromKeyedServices(Clients.Acme)] IConfiguration acme) => new
{
    S3Url = acme["s3_url"],        // "B1"
    Theme = acme["theme"],         // "light"
    Language = acme["language"],   // "en" — фолбэк на ядро
});

// Обычный сервис: та же keyed-инъекция в конструкторе.
public sealed class AcmeStorage([FromKeyedServices(Clients.Acme)] IConfiguration configuration)
{
    public string S3Url => configuration["s3_url"]!;
}

// Код, у которого есть IServiceProvider:
IConfiguration beta = app.Services.GetRequiredKeyedService<IConfiguration>(Clients.Beta);
beta["s3_url"];                    // "C"
```

Ключ — это id клиента из `client_settings.client_id` (или дочерний ключ секции), сравнение регистронезависимое. Инъекция выполняется при каждом разрешении, поэтому клиент, добавленный или обновлённый позже, виден на следующем запросе без перерегистрации. Keyed-регистрация использует `KeyedService.AnyKey`, так что работает и имя, известное только в рантайме. Перечисление keyed-сервисов не перечисляет клиентов — для этого есть `IClientConfigurations.Clients`.

Имя, известное только в рантайме, передаётся в реестр:

```csharp
app.MapGet("/settings/{client}", (string client, IClientConfigurations clients) =>
    new { Client = client, S3Url = clients.GetClient(client)["s3_url"] });
```

**Подключение и удаление в рантайме.**

```csharp
await clients.AttachAsync("acme", new MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?> { ["s3_url"] = "B1" },
});

await clients.RemoveAsync("acme");   // представление снова падает на значения ядра
```

`AttachAsync` заменяет существующий оверлей того же клиента и освобождает заменённый. `RemoveAsync` возвращает `false` и ничего не меняет для клиента, которого по-прежнему отдаёт провайдер: его оверлей всё равно был бы собран заново при следующем обращении. Удалить можно только вложение, добавленное в рантайме.

Представления, возвращённые `GetClient`, «живые»: при каждом обращении они спрашивают у реестра текущий корень, поэтому attach/remove/reload виден через ссылку, полученную раньше. `GetReloadToken()` такого представления композитит ядро, оверлей и реестр, поэтому колбэк срабатывает и на attach/remove. Реестр потокобезопасен.

### Builder: методы и порядок

| Метод | Что добавляет |
|-------|---------------|
| `UseStandardSources(...)` | `appsettings.json`, `appsettings.{env}.json`, переменные окружения, аргументы. **Идемпотентен**: в ASP.NET Core / generic host хост уже добавил их, и метод ничего не делает |
| `UseSecrets(...)` | прежний конвейер секретов: плейсхолдеры, алиасы, источник аргументов |
| `UseDynamic(source)` | источник, значения которого меняются в рантайме; ставится после статических |
| `Use(source)` | любой `IConfigurationSource` — для источников без отдельного метода |
| `Finalize()` | переносит проход в конец; нужно, только если источники добавляются напрямую в `sa.Builder` |

Источник, зарегистрированный после последнего вызова Sa, — обычный провайдер и выигрывает у прохода для своих ключей. `Finalize()` (или `sa.Builder.Add(...)`, затем `Finalize()`) — явный способ вернуть порядок.

### Обновление динамики: опции

| Опция | По умолчанию | Смысл |
|-------|--------------|-------|
| `InitialLoad` | `Explicit` | `Explicit` — ничего не происходит, пока вы не вызовете `EnsureInitializedAsync`; `Hosted` — однократный хук грузит до первого запроса, ошибка валит старт; `Background` — хост стартует сразу, ошибка логируется |
| `DynamicReload` | `false` | регистрирует ровно одну hosted-службу с `PeriodicTimer`; без неё таймера нет |
| `DynamicReloadInterval` | 30 с | период опроса |
| `DynamicReloadInitialDelay` | `null` | задержка перед первым опросом (по умолчанию — один интервал) |

Позднее связывание (`PgConfigurationConnection.FromServices()`) резолвится из контейнера во время `EnsureInitializedAsync`, то есть после `builder.Build()`; при явных `DataSource`/`ConnectionString` шаг инициализации не нужен. Инициализация идемпотентна; неудачный периодический reload просто логируется, а предыдущий снимок остаётся в силе.

### Кодировки и формат значений

Кодировки, формат `ENC1:`, длина payload, поведение с Unicode и правила `.env` описаны в [SecretStore/Readme-ru.md](SecretStore/Readme-ru.md#кодировки-и-формат-значений).

## Лицензия

MIT
