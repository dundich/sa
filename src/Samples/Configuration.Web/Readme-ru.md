# Configuration.Web

**Русский** | [English](README.md)

ASP.NET Core Minimal API sample, демонстрирующий работу **Sa.Configuration** — разбор аргументов командной строки, управление секретами из файлов и переменных окружения с подстановкой `{{placeholder}}`, а также динамическая конфигурация из PostgreSQL.

---

## Содержание

- [Быстрый старт](#быстрый-старт)
- [Что Демонстрирует Этот Пример](#что-демонстрирует-этот-пример)
- [Цепочка Разрешения Секретов](#цепочка-разрешения-секретов)
  - [Пример: secrets.txt](#пример-secretstxt)
  - [Пример: appsettings.json с Плейсхолдерами](#пример-appsettingsjson-с-плейсхолдерами)
- [Ключевой Код](#ключевой-код)
  - [Program.cs](#programcs)
  - [Ожидаемый Ответ](#ожидаемый-ответ)
- [Зависимости](#зависимости)
- [Лицензия](#лицензия)

## Быстрый старт

```bash
# 1. Запустите PostgreSQL (или используйте общий docker-compose из Samples)
cd src/Samples
docker compose up db -d

# 2. Запустите пример
dotnet run --project Samples/Configuration.Web
```

Откройте `http://localhost:5245/settings` в браузере, чтобы увидеть все значения конфигурации, загруженные из трёх источников: аргументы CLI, файл секретов и база данных PostgreSQL.

---

## Что Демонстрирует Этот Пример

1. **Управление Секретами** — плейсхолдеры `{{sa_secret}}` в `appsettings.json` разрешаются из цепочки хранилищ: переменные окружения → аргументы CLI → файл `secrets.txt`.
2. **Динамическая Конфигурация из PostgreSQL** — общие настройки (`s3_url`, `theme`, `language`, `notifications`) хранятся в таблице `setting` и отражаются внутри приложения без перезапуска.
3. **Настройки клиентов** — общие значения лежат в `setting`, переопределения каждого клиента — в `client_settings` с разбиением по `client_id`; `GET /settings/{client}` накладывает их поверх ядра, а фоновая служба не регистрируется. Известного клиента можно ещё и внедрить по ключу (`GET /settings/acme`).
4. **Разбор Аргументов Командной Строки** — класс `Arguments` подключается через `AddSaConfiguration()` для переопределения секретов из CLI.
5. **Без ORM** — ни EF Core, ни миграций. Только чистый Npgsql с `CREATE TABLE IF NOT EXISTS`.

---

## Цепочка Разрешения Секретов

Формат плейсхолдера `{{key}}` разрешает значения из цепочки хранилищ по порядку:

1. **Переменные Окружения** — например, `SA_SECRET="Мой Секрет"`
2. **Аргументы CLI** — например, `/sa_secret:"Переопределение из CLI"`
3. **secrets.txt** — текстовый файл с парами `ключ=значение` (комментарии с `#`, автоматическая обрезка кавычек)

Опциональный вариант `{{?key}}` возвращает `null` вместо исключения, если ключ отсутствует.

### Пример: secrets.txt

```
sa_pg_host=localhost
sa_pg_user=postgres
sa_pg_password=postgres
sa_pg_port=5432
sa_pg_database=postgres
sa_pg_schema=public

sa_secret= "ТОП СЕКРЕТ!"
```

### Пример: appsettings.json с Плейсхолдерами

```json
{
  "secret": "{{sa_secret}}",
  "sa": {
    "pg": {
      "connection": "User ID={{sa_pg_user}};Password={{sa_pg_password}};Host={{sa_pg_host}}"
    }
  }
}
```

---

## Ключевой Код

### Program.cs

```csharp
var builder = WebApplication.CreateSlimBuilder(args);

// Статический слой: хост уже добавил appsettings/env/args. Sa добавляет pass секретов.
builder.Configuration.AddSaConfiguration(sa => sa.UseSecrets());

const string PG_KEY = "sa:pg:connection";
string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

using var ds = IPgDataSource.Create(connectionString);

// Две таблицы: `setting` хранит общие значения ядра, `client_settings` — переопределения
// клиентов с разбиением по client_id. Имена ключей в обеих таблицах одинаковые.
ds.ExecuteScalar("""
    CREATE TABLE IF NOT EXISTS setting (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );

    INSERT INTO setting (key, value) VALUES
        ('s3_url',        'A'),
        ('theme',         'dark'),
        ('language',      'en'),
        ('notifications', 'enabled')
    ON CONFLICT DO NOTHING;

    CREATE TABLE IF NOT EXISTS client_settings (
        client_id TEXT NOT NULL,
        key       TEXT NOT NULL,
        value     TEXT NOT NULL,
        PRIMARY KEY (client_id, key)
    );

    INSERT INTO client_settings (client_id, key, value) VALUES
        ('acme', 's3_url',        'B1'),
        ('acme', 'theme',         'light'),
        ('acme', 'notifications', 'disabled'),
        ('beta', 's3_url',        'C'),
        ('beta', 'language',      'de')
    ON CONFLICT DO NOTHING;
""", null).Wait();

// Динамический слой: значения ядра приходят из `setting`, через пул приложения.
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.DataSource(ds),
    new PostgreSqlConfigurationOptions
    {
        SelectSql = "SELECT key, value FROM setting",
    });

// Клиентский слой: один запрос возвращает строки всех клиентов, реестр группирует их по
// client_id и строит корень на клиента. Ключи хранятся как есть — без префикса.
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IPgDataSource>(static (dataSource, cancellationToken) =>
        ReadClientSettingsAsync(dataSource, cancellationToken));
});

builder.Services.AddSingleton(ds);

// Известного клиента можно внедрить по ключу: конструктор сервиса получает корень acme.
builder.Services.AddSingleton<AcmeStorage>();

// Runtime: обновление по требованию (фонового таймера по умолчанию нет). Один
// ReloadDynamicAsync обновляет и ядро, и корни клиентов.
builder.Services.AddSaConfigurationRuntime(builder.Configuration);

var app = builder.Build();

var settingsApi = app.MapGroup("/settings");

// Базовые (ядерные) значения и клиенты, которые вернул провайдер.
settingsApi.MapGet("/", (IConfiguration configuration, IClientConfigurations clients) => new Settings[] {
    new (Key: PG_KEY, Value: MaskPassword(configuration[PG_KEY])),
    new (Key: "s3_url", Value: configuration["s3_url"]),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"]),
    new (Key: "clients", Value: string.Join(", ", clients.Clients))
});

// Один клиент: его корень поверх значений ядра. Неизвестный клиент читает только ядро.
settingsApi.MapGet("/{client}", (string client, IClientConfigurations clients) =>
{
    IConfiguration clientConfiguration = clients.GetClient(client);

    return new Settings[] {
        new (Key: "s3_url", Value: clientConfiguration["s3_url"]),
        new (Key: "theme", Value: clientConfiguration["theme"]),
        new (Key: "language", Value: clientConfiguration["language"]),
        new (Key: "notifications", Value: clientConfiguration["notifications"]),
        new (Key: "secret", Value: clientConfiguration["secret"]),
    };
});

// Keyed-инъекция для известного клиента: корень клиента приходит через конструктор, обработчик
// не обращается к реестру. `/settings/acme` — литеральный маршрут, поэтому он выигрывает у
// `/settings/{client}`.
settingsApi.MapGet("/acme", (AcmeStorage acme) => new Settings[] {
    new (Key: "s3_url", Value: acme.S3Url),
    new (Key: "theme", Value: acme.Theme),
    new (Key: "language", Value: acme.Language),
    new (Key: "notifications", Value: acme.Notifications),
});

// Обновляем только динамический слой: файлы на диске не перечитываются. Провайдеры клиентов
// перечитываются тем же вызовом.
settingsApi.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
{
    await reloader.ReloadDynamicAsync();
    return Results.Ok();
});

// Провайдер клиентов: плоские строки (client_id, key, value), прочитанные одним запросом.
static async ValueTask<IReadOnlyList<ClientSetting>> ReadClientSettingsAsync(
    IPgDataSource dataSource,
    CancellationToken cancellationToken)
{
    List<ClientSetting> rows = [];

    await dataSource.ExecuteReaderAsync(
        "SELECT client_id, key, value FROM client_settings",
        (reader, _) => rows.Add(new ClientSetting(
            reader.GetString(0), reader.GetString(1), reader.GetString(2))),
        initCommand: null,
        cancellationToken);

    return rows;
}

public static class DemoClients
{
    public const string Acme = "acme";
}

// Известный клиент, который резолвится из контейнера по ключу: конструктор получает собственный
// корень acme, уже наложенный поверх значений ядра.
public sealed class AcmeStorage([FromKeyedServices(DemoClients.Acme)] IConfiguration configuration)
{
    public string? S3Url => configuration["s3_url"];
    public string? Theme => configuration["theme"];
    public string? Language => configuration["language"];
    public string? Notifications => configuration["notifications"];
}
```

### Ожидаемые ответы

`GET /settings` — значения ядра и клиенты, которые вернул провайдер:

```json
[
  { "key": "sa:pg:connection", "value": "Username=postgres;Password=***;Host=localhost;Port=5432;Database=postgres;Pooling=True;Search Path=public;Command Timeout=180" },
  { "key": "s3_url",           "value": "A" },
  { "key": "theme",            "value": "dark" },
  { "key": "language",         "value": "en" },
  { "key": "notifications",    "value": "enabled" },
  { "key": "secret",           "value": "ENCRYPTED DEMO SECRET" },
  { "key": "clients",          "value": "acme, beta" }
]
```

`GET /settings/acme` — литеральный маршрут: корень acme внедрён в `AcmeStorage` по ключу.
Меняются только существующие строки, и этот маршрут отдаёт лишь четыре поля, которые читает сервис:

```json
[
  { "key": "s3_url",        "value": "B1" },
  { "key": "theme",         "value": "light" },
  { "key": "language",      "value": "en" },
  { "key": "notifications", "value": "disabled" }
]
```

`GET /settings/beta` — маршрут `{client}`: `s3_url` = `C`, `language` = `de`, остальное из ядра.

Пароль маскируется: `NpgsqlConnectionStringBuilder` нормализует ключевые слова, поэтому в выводе
`Username=…` / `Search Path=…`, а не `User ID=…` / `SearchPath=…` из `secrets.txt`. Это
перерисовка строки подключения, а не исходная строка.

---

## Зависимости

| Пакет | Назначение |
|-------|-----------|
| `Sa.Configuration` | Управление секретами, настройки клиентов, разбор аргументов CLI |
| `Sa.Configuration.PostgreSql` | Динамическая конфигурация из PostgreSQL, включая настройки клиентов |
| `Sa.Data.PostgreSql` | Обёртка клиента Npgsql |
| `Npgsql` | `NpgsqlConnectionStringBuilder` для маскирования пароля; приходит транзитивно, но сэмпл использует его напрямую |
| `Microsoft.AspNetCore.OpenApi` | Поддержка OpenAPI (только для разработки) |

---

## Лицензия

MIT
