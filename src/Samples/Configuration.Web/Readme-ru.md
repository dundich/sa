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
2. **Динамическая Конфигурация из PostgreSQL** — настройки приложения (`theme`, `language`, `notifications`) хранятся в таблице БД и отражаются внутри приложения без перезапуска.
3. **Разбор Аргументов Командной Строки** — класс `Arguments` подключается через `AddSaConfiguration()` для переопределения секретов из CLI.
4. **Без ORM** — ни EF Core, ни миграций. Только чистый Npgsql с `CREATE TABLE IF NOT EXISTS`.

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

// Статический слой: хост уже добавил appsettings/env/args. Sa добавляет pass секретов и
// клиентский оверлей.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets();
    sa.UseClients();
});

const string PG_KEY = "sa:pg:connection";
string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

using var ds = IPgDataSource.Create(connectionString);

// Одна таблица для всего: client_id NULL — базовые значения, непустой client_id — переопределения
// этого клиента.
ds.ExecuteScalar("""
    CREATE TABLE IF NOT EXISTS settings (
        client_id TEXT,
        key       TEXT NOT NULL,
        value     TEXT NOT NULL
    );

    CREATE UNIQUE INDEX IF NOT EXISTS settings_client_key
        ON settings ((COALESCE(client_id, '')), key);

    INSERT INTO settings (client_id, key, value) VALUES
        (NULL,   'theme',         'dark'),
        (NULL,   'language',      'en'),
        (NULL,   'notifications', 'enabled'),
        ('acme', 'theme',         'light'),
        ('acme', 'notifications', 'disabled'),
        ('beta', 'language',      'de')
    ON CONFLICT DO NOTHING;
""", null).Wait();

// Динамический слой: пул приложения, поэтому второй пул не открывается, а явная стратегия
// делает значения доступными сразу.
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.DataSource(ds),
    new PostgreSqlConfigurationOptions(
        // Порядок колонок — часть контракта: client, key, value.
        SelectSql: "SELECT client_id, key, value FROM settings")
    {
        ClientColumn = "client_id",
    });

// Runtime: обновление по требованию (фонового таймера по умолчанию нет) и keyed IConfiguration
// на каждого клиента.
builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.KeyedClientConfigurations = true;
});

var app = builder.Build();

var settingsApi = app.MapGroup("/settings");

// Базовые значения + клиенты, объявленные в переопределениях.
settingsApi.MapGet("/", (IConfiguration configuration) => new Settings[] {
    new (Key: PG_KEY, Value: MaskPassword(configuration[PG_KEY])),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"]),
    new (Key: "clients", Value: string.Join(", ", configuration.GetClientNames()))
});

// Один клиент: его переопределения поверх базовых значений. Неизвестный клиент читает базу.
settingsApi.MapGet("/{client}", (string client, IConfiguration configuration) =>
{
    IConfiguration view = configuration.ForClient(client);
    return new Settings[] {
        new (Key: "theme", Value: view["theme"]),
        new (Key: "language", Value: view["language"]),
        new (Key: "notifications", Value: view["notifications"]),
    };
});

// Обновляем только динамический слой: файлы на диске не перечитываются.
settingsApi.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
{
    await reloader.ReloadDynamicAsync();
    return Results.Ok();
});
```

### Ожидаемые ответы

`GET /settings` — базовые значения и найденные клиенты:

```json
[
  { "key": "sa:pg:connection", "value": "Username=postgres;Password=***;Host=localhost;Port=5432;Database=postgres;Pooling=True;Search Path=public;Command Timeout=180" },
  { "key": "theme",             "value": "dark" },
  { "key": "language",          "value": "en" },
  { "key": "notifications",     "value": "enabled" },
  { "key": "secret",            "value": "ENCRYPTED DEMO SECRET" },
  { "key": "clients",           "value": "acme, beta" }
]
```

`GET /settings/acme` — оверлей (меняется только то, что перекрывает acme):

```json
[
  { "key": "theme",             "value": "light" },
  { "key": "language",          "value": "en" },
  { "key": "notifications",     "value": "disabled" }
]
```

`GET /settings/beta` — `"language": "de"`, остальное из базы.

Пароль маскируется: `NpgsqlConnectionStringBuilder` нормализует ключевые слова, поэтому в выводе
`Username=…` / `Search Path=…`, а не `User ID=…` / `SearchPath=…` из `secrets.txt`. Это
перерисовка строки подключения, а не исходная строка.

---

## Зависимости

| Пакет | Назначение |
|-------|-----------|
| `Sa.Configuration` | Управление секретами, разбор аргументов CLI |
| `Sa.Configuration.PostgreSql` | Динамическая конфигурация из PostgreSQL |
| `Sa.Data.PostgreSql` | Обёртка клиента Npgsql |
| `Npgsql` | `NpgsqlConnectionStringBuilder` для маскирования пароля; приходит транзитивно, но сэмпл использует его напрямую |
| `Microsoft.AspNetCore.OpenApi` | Поддержка OpenAPI (только для разработки) |

---

## Лицензия

MIT
