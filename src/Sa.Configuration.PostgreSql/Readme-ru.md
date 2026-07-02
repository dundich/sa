# Sa.Configuration.PostgreSql

Динамический источник конфигурации для .NET, загружающий настройки из PostgreSQL. Изменения в БД применяются к работающему приложению без перезапуска — достаточно вызвать `Reload()` на `IConfigurationRoot`.

---

## Возможности

- **Живая конфигурация**: значения хранятся в БД и могут быть изменены во время выполнения
- **Параметризированные SQL-запросы**: поддержка `@named_parameters` через `NpgsqlParameter`
- **Автоматические повторы**: встроенная стратегия повторов (`PgRetryStrategy`) с детекцией транзитных ошибок Npgsql
- **Обрезка ключей/значений**: пробелы автоматически обрезаются и у ключей, и у значений
- **Безопасная обработка NULL**: `NULL` в БД → `null` в конфиге; пустая строка → `string.Empty`

---

## Публичный API

| Тип | Назначение |
|-----|-----------|
| `PostgreSqlConfigurationOptions` | Immutable record: `ConnectionString`, `SelectSql`, `Parameters` |
| `DatabaseConfigurationSource` | Реализация `IConfigurationSource` |
| `DatabaseConfigurationProvider` | `ConfigurationProvider`, загружающий пары ключ-значение из БД |
| `Setup.AddSaPostgreSqlConfiguration()` | Метод-расширение для `IConfigurationBuilder` |

---

## Быстрый старт

```csharp
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "Host=localhost;Database=myapp;Username=app;Password=secret",
    SelectSql: "SELECT key, value FROM app_settings"
));

var app = builder.Build();

// Чтение настроек
var theme = app.Configuration["theme"];          // → "dark"
var lang  = app.Configuration["language"];       // → "en"
```

---

## Параметризированные запросы

Используйте `@parameters` для фильтрации по клиенту/арендатору:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM client_settings WHERE client_id = @client_id",
    Parameters: [new NpgsqlParameter("client_id", "acme-corp")]
));
```

---

## Обновления живой конфигурации

Когда строки в таблице `app_settings` изменяются, приложение может подхватить новые значения:

```csharp
// После изменения строк в базе данных:
((IConfigurationRoot)app.Configuration).Reload();

// Или вручную:
provider.Reload();  // DatabaseConfigurationProvider реализует IConfigurationProvider
```

---

## Поведение загрузки

| Сценарий | Результат |
|----------|----------|
| Ключ пустой или состоит только из пробелов | Пропускается |
| Значение `NULL` в БД | Сохраняется как `null` |
| Значение пустая строка в БД | Сохраняется как `string.Empty` |
| Ошибка подключения | `InvalidOperationException` с оригинальным исключением как `InnerException` |

---

## Схема таблицы

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
    ('debug_mode', '');   -- пустая строка
```

---

## Зависимости

- `Microsoft.Extensions.Configuration`
- `Sa.Data.PostgreSql` (обёртка Npgsql с PgRetryStrategy и IPgDataSource)

---

## Структура проекта

```
src/Sa.Configuration.PostgreSql/
├── PostgreSqlConfigurationOptions.cs   # Record опций
├── DatabaseConfigurationSource.cs      # IConfigurationSource
├── DatabaseConfigurationProvider.cs    # ConfigurationProvider + повторы
├── Setup.cs                            # Метод-расширение AddSaPostgreSqlConfiguration()
└── Readme.md                           # ← вы здесь
```

---

## Лицензия

MIT
