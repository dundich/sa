# Sa.Configuration.PostgreSql

Динамический источник конфигурации для .NET, загружающий настройки из PostgreSQL. Изменения в БД применяются к работающему приложению без перезапуска — достаточно вызвать `Reload()` на `IConfigurationRoot`.

---

## Возможности

- **Живая конфигурация**: значения хранятся в БД и могут быть изменены во время выполнения
- **Параметризированные SQL-запросы**: поддержка `@named_parameters` через `NpgsqlParameter` — параметры клонируются при каждой загрузке, поэтому один экземпляр options переиспользуем
- **Автоматические повторы**: транзиентные ошибки Npgsql (сетевые сбои, SQLSTATE `08xxx`) повторяются с decorrelated jitter back-off
- **Один пул соединений, а не новый на каждый reload**: пул создаётся один раз и переиспользуется всеми `Reload()`
- **Асинхронная перезагрузка**: `LoadAsync(CancellationToken)` для таймеров и `BackgroundService`
- **Обрезка ключей/значений**: пробелы автоматически обрезаются и у ключей, и у значений
- **Контроль слоёв**: решайте, будут ли пустые и `NULL` значения делегироваться источникам с меньшим приоритетом или перекрывать их

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

Опции валидируются в момент регистрации источника, поэтому пустая строка подключения или запрос
падают ещё на этапе сборки хоста, а не при первой загрузке.

---

## Параметризованные запросы

Используйте `@parameters` для фильтрации по клиенту/арендатору:

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM client_settings WHERE client_id = @client_id",
    Parameters: [new NpgsqlParameter("client_id", "acme-corp")]
));
```

---

## Переиспользование пула приложения

По умолчанию источник владеет собственным источником данных. Чтобы использовать один общий пул
со всем приложением, создайте его явно и передайте в источник конфигурации:

```csharp
IPgDataSource appDataSource = IPgDataSource.Create(connectionString);

builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions(
        ConnectionString: string.Empty,   // игнорируется: подключение несёт источник данных
        SelectSql: "SELECT key, value FROM app_settings"),
    appDataSource);
```

Если приложение уже регистрирует `IPgDataSource` через `AddSaPostgreSqlDataSource`
(`o => o.WithConnectionString(...)`), получите этот экземпляр и передайте его источнику
конфигурации — так оба пути используют один пул соединений.

Провайдер никогда не освобождает источник данных, который не создавал — владение
`appDataSource` остаётся за вами. Собственный источник освобождается при Dispose'е
`IConfigurationRoot`.

---

## Обновления живой конфигурации

Когда строки в таблице `app_settings` изменены, приложение может подхватить новые значения:

```csharp
// После изменения строк в базе данных:
((IConfigurationRoot)app.Configuration).Reload();

// Из таймера или BackgroundService, без блокировки потока:
await provider.LoadAsync(cancellationToken);
```

`Reload()` заменяет весь набор ключей: удалённые из БД строки исчезают из конфигурации, новые появляются. **Неудачная** загрузка оставляет предыдущий снимок на месте — транзиентная проблема с БД никогда не обнуляет работающую конфигурацию.

`LoadAsync` — асинхронный аналог `Load()`, дополнительно поднимающий reload-токен провайдера, поэтому потребители, удерживающие сам провайдер (а не `IConfigurationRoot`, который поднимает свой), увидят новый снимок. Конкурентные перезагрузки схлопываются в один запрос.

---

## Опции

| Опция | По умолчанию | Значение |
|-------|---------------|----------|
| `MaxAttempts` | `3` | Всего попыток загрузки, включая первую. `0` и `1` означают одну попытку |
| `MedianFirstRetryDelay` | `530` | Медианная задержка первого повтора в мс; каждая задержка джиттерится. Первый повтор всегда мгновенный |
| `SkipEmptyValues` | `true` | Отбрасывать строки, у которых **значение** `NULL`/пустое. См. *Слои* ниже. Пустые **ключи** отбрасываются независимо от этой настройки |
| `LastWins` | `true` | При дублях ключей побеждает последняя строка. `false` — первая |

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM app_settings") with
{
    MaxAttempts = 5,
    MedianFirstRetryDelay = 200,
    SkipEmptyValues = false,
    LastWins = true,
});
```

`ToString()` у options переопределён: значения параметров только считаются, но не выводятся, а пароль в строке подключения маскируется — так что options безопасно интерполировать в логи.

---

## Поведение загрузки

| Сценарий | Результат |
|----------|-----------|
| Ключ `NULL` | Пропускается — всегда, независимо от `SkipEmptyValues` |
| Ключ пустой или только из пробелов | Пропускается — всегда, независимо от `SkipEmptyValues` |
| Значение `NULL` в БД | По умолчанию пропускается — см. *Слои* |
| Значение пустая строка или только пробелы | По умолчанию пропускается — см. *Слои* |
| Ключ или значение имеют пробелы по краям | Обрезаются |
| Запрос вернул меньше 2 колонок | `InvalidOperationException` с фактическим числом колонок |
| Транзиентная ошибка подключения | Повторяется до `MaxAttempts` |
| Перманентная ошибка запроса | Падает на первой попытке |
| Ошибка после повторов | `InvalidOperationException` с оригинальным исключением как `InnerException` |
| Отмена загрузки | `OperationCanceledException` — никогда не оборачивается |
| Дубли ключей в результате | Побеждает последняя строка (`LastWins`) |

### Слои

`IConfiguration` берёт значение из источника с наивысшим приоритетом, который этот ключ содержит. При `SkipEmptyValues = true` (по умолчанию) значение `NULL` или пустая строка в БД означают, что источник просто не содержит этого ключа, и за него отвечает источник с меньшим приоритетом:

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

Учтите, что значение `NULL` не может перекрыть источник с меньшим приоритетом: `ConfigurationRoot` продолжает обход провайдеров, пока они возвращают `null`. Перекрывает только пустая строка.

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
    ('debug_mode', '');   -- по умолчанию пропускается: делегируется источникам ниже
```

`PRIMARY KEY` на `key` не обязателен — без него дубли разрешаются по правилу `LastWins`.

---

## Зависимости

- `Microsoft.Extensions.Configuration`
- `Npgsql` (`NpgsqlParameter` входит в публичный API)
- `Sa.Data.PostgreSql` (обёртка Npgsql с `PgRetryStrategy` и `IPgDataSource`)

---

## Лицензия

MIT
