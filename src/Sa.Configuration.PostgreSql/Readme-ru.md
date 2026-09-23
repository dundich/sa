# Sa.Configuration.PostgreSql

Динамический источник конфигурации для .NET, загружающий настройки из PostgreSQL. Изменения в БД применяются к работающему приложению без перезапуска — достаточно вызвать `Reload()` на `IConfigurationRoot`.

---

## Возможности

- **Живая конфигурация**: значения хранятся в БД и могут быть изменены во время выполнения
- **Параметризированные SQL-запросы**: поддержка `@named_parameters` через `NpgsqlParameter` — параметры клонируются при каждой загрузке, поэтому один экземпляр options переиспользуем
- **Автоматические повторы**: встроенная стратегия повторов (`PgRetryStrategy`) с детекцией транзитных ошибок Npgsql
- **Обрезка ключей/значений**: пробелы автоматически обрезаются и у ключей, и у значений
- **Пропуск пустых значений**: ключи или значения, равные `NULL`, пустые или состоящие только из пробелов, не добавляются в конфигурацию

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

`Reload()` заменяет весь набор ключей: удалённые из БД строки исчезают из конфигурации, новые появляются.

---

## Поведение загрузки

| Сценарий | Результат |
|----------|----------|
| Ключ пустой или состоит только из пробелов | Пропускается |
| Значение `NULL` в БД | Пропускается — ключ не добавляется в конфигурацию |
| Значение пустая строка или только пробелы | Пропускается — ключ не добавляется в конфигурацию |
| Ключ или значение имеют пробелы по краям | Обрезаются |
| Ошибка подключения/запроса | `InvalidOperationException` с оригинальным исключением как `InnerException` |

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
    ('debug_mode', '');   -- пропускается: не добавляется в конфигурацию
```

---

## Зависимости

- `Microsoft.Extensions.Configuration`
- `Npgsql` (`NpgsqlParameter` входит в публичный API)
- `Sa.Data.PostgreSql` (обёртка Npgsql с `PgRetryStrategy` и `IPgDataSource`)

---

## Лицензия

MIT
