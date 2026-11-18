# Sa.Configuration.PostgreSql

**Русский** | [English](Readme.md)

Источник конфигурации, который читает настройки из таблицы PostgreSQL. Значения можно менять прямо в базе, а приложение подхватит их через `ReloadDynamicAsync()` — без редеплоя и рестарта.

Пакет дополняет **[Sa.Configuration](../Sa.Configuration)**: секреты в плейсхолдерах, алиасы ключей и клиентские оверлеи приходят из базового пакета, здесь — только источник данных и его настройки.

## Содержание

- [Установка](#установка)
- [Быстрый старт](#быстрый-старт)
- [Стратегии подключения](#стратегии-подключения)
- [Ключи и разделители](#ключи-и-разделители)
- [Алиасы ключей](#алиасы-ключей)
- [Параметризованные запросы](#параметризованные-запросы)
- [Клиенты](#клиенты)
- [Обновление живой конфигурации](#обновление-живой-конфигурации)
- [Подробности](#подробности)
- [Лицензия](#лицензия)

## Установка

```bash
dotnet add package Sa.Configuration.PostgreSql
```

## Быстрый старт

```csharp
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Стратегия подключения отвечает «откуда», опции — «что читать».
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.ConnectionString("Host=localhost;Database=myapp;Username=app;Password=secret"),
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));

var app = builder.Build();

// Чтение настроек
var theme = app.Configuration["theme"];          // → "dark"
var lang  = app.Configuration["language"];       // → "en"
```

Короткая форма `AddSaPostgreSqlConfiguration(options)` использует `PgConfigurationConnection.FromServices()`: зарегистрированный в приложении `IPgDataSource` (или `NpgsqlDataSource`) переиспользуется, второй пул не создаётся.

Опции проверяются сразу при регистрации источника, поэтому пустой запрос падает на этапе сборки хоста, а не при первой загрузке. Строку подключения валидирует её стратегия: `ConnectionString(...)` разбирает её сразу, `DataSource(...)` не принимает `null`.

## Стратегии подключения

У `PgConfigurationConnection` три формы — один и тот же вопрос «откуда данные», что и в остальном `Sa.Data.PostgreSql`:

| Стратегия | Владение | Когда подключение доступно |
|-----------|----------|----------------------------|
| `ConnectionString("…")` | источник создаёт и освобождает свой пул | сразу, при регистрации |
| `DataSource(IPgDataSource)` | владелец — вызывающий | сразу, при регистрации |
| `FromServices("key"?)` — **по умолчанию** | пул приложения (`PgDataSourceResolver`: keyed `IPgDataSource` → обычный `IPgDataSource` → `NpgsqlDataSource`) | с первой инициализации runtime |

Общий пул приложения:

```csharp
IPgDataSource appDataSource = IPgDataSource.Create(connectionString);

builder.Configuration.AddSaPostgreSqlConfiguration(
    appDataSource,
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
```

Либо пусть источник возьмёт зарегистрированный — ничего не создаётся и ничего не освобождается:

```csharp
builder.Services.AddSaPostgreSqlDataSource(b => b.FromConfiguration("Postgres"));

builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));

builder.Services.AddSaConfigurationRuntime(builder.Configuration);

var app = builder.Build();
await app.Services.GetRequiredService<ISaConfigurationReloader>()
    .EnsureInitializedAsync(app.Lifetime.ApplicationStopping);
```

`FromServices` — **позднее связывание**: конфигурация собирается до появления контейнера, поэтому источник резолвится при первой инициализации runtime. До этого слой БД не отдаёт ключей, значит такой источник нужно инициализировать до первого запроса (явно или хуком хоста — см. `SaDynamicInitialLoad`). Если значения нужны, пока строится `IServiceCollection`, используйте `ConnectionString`/`DataSource`. `AsOptional()` превращает нерезолвящийся источник в пустой слой вместо упавшего старта.

## Ключи и разделители

Разделитель конфигурации — `:`, поэтому вложенная настройка читается как `configuration["sa:pg:host"]`.

По умолчанию источник хранит ключи **как есть** и ничего не переформатирует. Если база заполняется из тех же мест, что и переменные окружения (где разделитель — `__`), включите конвенцию окружения:

```csharp
var options = new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings")
{
    DoubleUnderscoreAsSeparator = true,
};
```

```sql
INSERT INTO app_settings (key, value) VALUES
    ('sa__pg__host', 'db.internal'),   -- читается как sa:pg:host
    ('sa:pg:port',   '5432');          -- читается как sa:pg:port
```

Псевдоним работает в обе стороны: ключи хранятся в канонической форме с `:`, но `configuration["sa__pg__host"]` тоже вернёт значение. Одиночное подчёркивание — это данные, а не разделитель (`config_db` остаётся `config_db`). Замена идёт попарно, как в `AddEnvironmentVariables()`: `a____b` превращается в `a::b`.

После нормализации два ключа могут обозначать одну настройку. Если запрос вернул обе записи одного пути, это дубли, и правило `LastWins` решает, какое значение останется.

## Алиасы ключей

Сам этот пакет ключи не маппит. Явные алиасы — связка вида «C#-путь `sa:scope:api:auth:base_url` ← плоский `auth_base_url`» — живут в Secrets-слое **[Sa.Configuration](../Sa.Configuration)** и применяются к суммарной конфигурации **всех** провайдеров:

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

Если этот PostgreSQL-источник зарегистрирован **после** прохода алиасов и сам хранит канонические пути, финализируйте конвейер — тогда алиасы лягут поверх него:

```csharp
builder.Configuration
    .AddSaPostgreSqlConfiguration(...)
    .AddSaConfiguration(...)
    .FinalizeSaConfiguration();
```

Полное поведение алиасов — в [Sa.Configuration](../Sa.Configuration/Readme-ru.md#алиасы-полные-правила).

## Параметризованные запросы

Используйте `@parameters`, чтобы фильтровать настройки по клиенту или арендатору:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM client_settings WHERE client_id = @client_id",
    Parameters: [new NpgsqlParameter("client_id", "acme-corp")]
));
```

Колонки читаются по позиции: запрос должен вернуть **ровно две** колонки, `(key, value)`. Всё остальное — ошибка на старте, в том числе трёхколоночная форма `(client, key, value)`: при чтении как `(key, value)` одна колонка беззвучно сдвинулась бы в другую. Параметры клонируются при каждой загрузке, поэтому один экземпляр options можно переиспользовать.

## Клиенты

Ядро читает общие значения; каждый клиент читает свои переопределения из такой же таблицы с фильтром по `client_id`. Имена ключей в обеих таблицах **одинаковые** — префикса и общего словаря нет, поэтому значения одного клиента не могут протечь ни в ядро, ни в другого клиента.

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

Ядро — обычный запрос с двумя колонками:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.FromServices(),
    new PostgreSqlConfigurationOptions("SELECT key, value FROM setting"));
```

Клиентский слой — один запрос, возвращающий `(client_id, key, value)` сразу по **всем** клиентам; реестр группирует строки по `client_id` и строит по корню на клиента. Шага discovery и запроса на каждого клиента нет.

```csharp
using Sa.Configuration.Clients;
using Sa.Data.PostgreSql;

public sealed class PgClientSettingsStore(IPgDataSource dataSource)
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

builder.Services.AddSingleton<PgClientSettingsStore>();

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    // Одна зависимость из DI (плюс токен) → строки по всем клиентам.
    clients.Add<PgClientSettingsStore>((store, ct) => store.LoadAllAsync(ct));
});
```

```csharp
IConfiguration core = app.Configuration;

core["s3_url"];                                       // "A"
clients.GetClient("client1")["s3_url"];               // "B1"
clients.GetClient("client2")["s3_url"];               // "C"
clients.GetClient("client1")["language"];             // "en" — фолбэк на ядро
clients.Clients;                                      // ["client1", "client2"]
```

Хранилище вызывается лениво при первом обращении и снова при каждом `ReloadDynamicAsync()`, поэтому строка, вставленная в `client_settings`, появляется без рестарта. Отдельный пул или другая база — это просто другой источник данных, который резолвит хранилище.

Правила наложения, keyed-инъекция и подключение/удаление в рантайме — в [Sa.Configuration](../Sa.Configuration/Readme-ru.md#клиенты-полные-правила).

## Обновление живой конфигурации

Когда строки в таблице изменены, приложение подхватывает новые значения так:

```csharp
// Обновить только слой БД — файлы на диске не перечитываются, а алиасы/плейсхолдеры
// пересчитываются от свежих значений:
await app.Services.GetRequiredService<ISaConfigurationReloader>()
    .ReloadDynamicAsync(cancellationToken);

// Полная перезагрузка (все провайдеры, включая файлы):
((IConfigurationRoot)app.Configuration).Reload();
```

Периодический опрос включается явно и регистрирует ровно одну hosted-службу:

```csharp
builder.Services.AddSaConfigurationRuntime(builder.Configuration, o =>
{
    o.DynamicReload = true;
    o.DynamicReloadInterval = TimeSpan.FromSeconds(15);
});
```

`Reload()` заменяет весь набор ключей: удалённые из БД строки исчезают, новые появляются. **Неудачная** загрузка оставляет предыдущий снимок на месте — временная проблема с БД никогда не обнулит работающую конфигурацию.

`LoadAsync` — асинхронный аналог `Load()`. Дополнительно он поднимает reload-токен провайдера, поэтому потребители, которые держат сам провайдер (а не `IConfigurationRoot`), увидят новый снимок. Конкурентные перезагрузки схлопываются в один запрос.

## Подробности

### Опции

| Опция | По умолчанию | Значение |
|-------|--------------|----------|
| `MaxAttempts` | `3` | Общее число попыток загрузки, включая первую. `0` и `1` означают одну попытку |
| `MedianFirstRetryDelay` | `530` | Медианная задержка первого повтора в мс; каждая задержка джиттерится. Первый повтор всегда мгновенный |
| `SkipEmptyValues` | `true` | Отбрасывать строки, у которых **значение** `NULL` или пустое. Подробнее — в разделе *Слои*. Пустые **ключи** отбрасываются независимо от этой настройки |
| `DoubleUnderscoreAsSeparator` | `false` | При `true` читать `__` в ключе как разделитель `:`, то есть `sa__pg__host` ≡ `sa:pg:host` |
| `LastWins` | `true` | При дублях ключей побеждает последняя строка. `false` — первая |

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.ConnectionString("..."),
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings") with
    {
        MaxAttempts = 5,
        MedianFirstRetryDelay = 200,
        SkipEmptyValues = false,
        DoubleUnderscoreAsSeparator = true,
        LastWins = true,
    });
```

При временных ошибках Npgsql (сетевые сбои, SQLSTATE `08xxx`) запрос повторяется по схеме decorrelated jitter back-off. Пул соединений создаётся один раз и переиспользуется всеми вызовами `Reload()`, а не заново на каждую загрузку.

`ToString()` переопределён и у options, и у стратегии подключения: значения параметров только считаются, но не выводятся, а пароль в строке подключения маскируется. Поэтому и то и другое безопасно интерполировать в логи.

### Поведение при загрузке

| Сценарий | Результат |
|----------|-----------|
| Ключ `NULL` | Пропускается — всегда, независимо от `SkipEmptyValues` |
| Ключ пустой или состоит только из пробелов | Пропускается — всегда, независимо от `SkipEmptyValues` |
| Значение `NULL` в БД | По умолчанию пропускается — см. *Слои* |
| Значение — пустая строка или только пробелы | По умолчанию пропускается — см. *Слои* |
| Пробелы по краям ключа или значения | Обрезаются |
| Ключ содержит `__` | Читается как разделитель `:` только при `DoubleUnderscoreAsSeparator = true` |
| Запрос вернул меньше 2 колонок | `InvalidOperationException` с фактическим числом колонок |
| Временная ошибка подключения | Повторяется до `MaxAttempts` |
| Постоянная ошибка запроса | Падает на первой попытке |
| Ошибка после всех повторов | `InvalidOperationException` с оригинальным исключением в `InnerException` |
| Отмена загрузки | `OperationCanceledException` — никогда не оборачивается |
| Дубли ключей в результате | Побеждает последняя строка (`LastWins`) |

### Слои

`IConfiguration` берёт значение из источника с наивысшим приоритетом, который содержит этот ключ. При `SkipEmptyValues = true` (по умолчанию) значение `NULL` или пустая строка в БД означают, что источник просто не содержит этого ключа, и за него отвечает источник с меньшим приоритетом:

```csharp
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["feature_x"] = "fallback"
});
// в БД feature_x = ''  →  configuration["feature_x"] == "fallback"
```

Установите `SkipEmptyValues = false`, чтобы сохранять эти значения и перекрывать всё, что ниже:

```csharp
// в БД feature_x = ''  →  configuration["feature_x"] == ""
```

Значение `NULL` не может перекрыть источник с меньшим приоритетом, потому что `ConfigurationRoot` продолжает обход провайдеров, пока они возвращают `null`. Перекрывает только пустая строка.

### Схема таблицы

Минимальная таблица, необходимая для провайдера:

```sql
CREATE TABLE app_settings (
    key   VARCHAR PRIMARY KEY,
    value TEXT
);

-- Пример данных
INSERT INTO app_settings (key, value) VALUES
    ('theme',      'dark'),
    ('language',   'en'),
    ('debug_mode', '');   -- по умолчанию пропускается: делегируется источникам ниже
```

`PRIMARY KEY` на `key` не обязателен — без него дубли разрешаются по правилу `LastWins`.

### Зависимости

- `Microsoft.Extensions.Configuration`
- `Npgsql` (`NpgsqlParameter` входит в публичный API)
- `Sa.Data.PostgreSql` (обёртка Npgsql с `PgRetryStrategy` и `IPgDataSource`)
- `Sa.Configuration` (маркер динамического источника, runtime/reloader и реестр клиентских конфигураций)

## Лицензия

MIT
