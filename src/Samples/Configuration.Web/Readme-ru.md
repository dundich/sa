# Configuration.Web

ASP.NET Core Minimal API sample, демонстрирующий работу **Sa.Configuration** — разбор аргументов командной строки, управление секретами из файлов и переменных окружения с подстановкой `{{placeholder}}`, а также динамическая конфигурация из PostgreSQL.

---

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

## Архитектура

```
AddSaConfiguration()
  ├── AddSaCommandLine(args)         → CommandLineArgsSecretStore
  ├── AddSaPostSecretProcessing()    → ChainedSecrets(
       │                               │     ├── EnvironmentVariableSecretStore
       │                               │     ├── CommandLineArgsSecretStore
       │                               │     └── FileSecretStore (secrets.txt)
       │                              )
  └── AddSaPostgreSqlConfiguration   → Динамические настройки из PostgreSQL
```

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

// Шаг 1: Подключаем секреты + CLI аргументы + переменные окружения
builder.Configuration.AddSaConfiguration();

const string PG_KEY = "sa:pg:connection";
string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

// Шаг 2: Создаём источник данных PostgreSQL
using var ds = IPgDataSource.Create(connectionString);

// Шаг 3: Создаём таблицу настроек (если не существует)
ds.ExecuteScalar("""
    CREATE TABLE IF NOT EXISTS settings (
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );
    INSERT INTO settings (key, value)
    VALUES ('theme', 'dark'), ('language', 'en'), ('notifications', 'enabled')
    ON CONFLICT (key) DO NOTHING;
""", null).Wait();

// Шаг 4: Добавляем PostgreSQL как источник динамической конфигурации
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions
(
    ConnectionString: connectionString,
    SelectSql: "select * from settings"
));

// Шаг 5: Регистрируем эндпоинты
var todosApi = app.MapGroup("/settings");
todosApi.MapGet("/", (IConfiguration configuration) => new Settings[] {
    new (Key: PG_KEY, Value: configuration[PG_KEY]),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"])
}).WithName("GetSettings");
```

### Ожидаемый Ответ

```json
[
  { "key": "sa:pg:connection", "value": "User ID=postgres;Password=postgres;..." },
  { "key": "theme",             "value": "dark" },
  { "key": "language",          "value": "en" },
  { "key": "notifications",     "value": "enabled" },
  { "key": "secret",            "value": "ТОП СЕКРЕТ!" }
]
```

---

## Зависимости

| Пакет | Назначение |
|-------|-----------|
| `Sa.Configuration` | Управление секретами, разбор аргументов CLI |
| `Sa.Configuration.PostgreSql` | Динамическая конфигурация из PostgreSQL |
| `Sa.Data.PostgreSql` | Обёртка клиента Npgsql |
| `Microsoft.AspNetCore.OpenApi` | Поддержка OpenAPI (только для разработки) |

---

## Лицензия

MIT
