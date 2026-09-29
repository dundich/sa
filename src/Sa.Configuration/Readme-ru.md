# Sa.Configuration

Безопасное управление секретами и парсер командной строки в экосистеме .NET `Microsoft.Extensions.Configuration`. Секреты автоматически подставляются в конфигурацию без ручного кода приложения.

---

## Возможности

- **Автоматическая подстановка секретов**: плейсхолдеры `{{key}}` заменяются реальными значениями из файлов, переменных окружения или аргументов командной строки
- **Нормализация значений**: значения секретов обрезаются; обрамляющие кавычки (`'`, `"`, `` ` ``) удаляются
- **Защита от циклов**: вложенные плейсхолдеры разрешаются до глубины 3; циклические ссылки бросают `InvalidOperationException`
- **Опциональные плейсхолдеры**: `{{?key}}` — если секрет не найден, вся строка становится `null` вместо исключения
- **Цепочка хранилищ**: несколько источников секретов с LIFO-приоритетом (последнее добавленное побеждает); `AddStore` потокобезопасен
- **Парсер аргументов**: поддерживает форматы `--key value`, `--key=value`, `-flag`; отрицательные числа считаются значениями; обрамляющие кавычки удаляются
- **Среды разработки**: автоматическая загрузка `secrets.{Environment}.txt` (Development/Staging/Production)

---

## Быстрый старт

### 1. Регистрация в `Program.cs`

```csharp
using Sa.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Подключение аргументов + секретов из файлов/ENV/CLI
builder.Configuration.AddSaConfiguration();

var app = builder.Build();
```

### 2. Файл секретов (`secrets.txt`)

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

> ⚠️ Добавьте `secrets*.txt` в `.gitignore`!

### 3. Плейсхолдеры в `appsettings.json`

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

### 4. Чтение конфигурации

```csharp
var pgConn = app.Configuration["sa:pg:connection"];
// → "User ID=postgres;Password=superSecret123;Host=localhost;..."
```

---

## Приоритет секретов

Секреты используют LIFO-приоритет — последнее добавленное хранилище побеждает. Цепочка по умолчанию разрешается в таком порядке:

| # | Источник | Пример |
|---|----------|--------|
| 1 | Аргументы командной строки | `--sa_pg_password=...` |
| 2 | Переменные окружения | `SA_PG_PASSWORD=...` |
| 3 | Файл конкретной среды | `secrets.Development.txt` |
| 4 | Базовый файл секретов | `secrets.txt` |

Первый источник со значением побеждает — командная строка может переопределить всё, переменные окружения переопределяют файлы, и т.д.

---

## Опциональные плейсхолдеры

Используйте `{{?key}}` вместо `{{key}}`, чтобы избежать ошибки при отсутствии секрета:

```json
{
  "optional_feature": "{{?feature_flag}}"
}
```

Если `feature_flag` не найден ни в одном хранилище, вся строка становится `null`.

В цепочке конфигурации (`AddSaConfiguration` / `AddSaPostSecretProcessing`) любой отсутствующий секрет — включая обычный `{{key}}` — даёт `null` для этого значения вместо исключения.

---

## Использование с Sa.Configuration.PostgreSql

```csharp
using Sa.Configuration;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Сначала стандартные источники (appsettings.json, secrets.txt)
builder.Configuration.AddSaConfiguration();

// Затем динамические настройки из базы данных
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM app_settings"
));

var app = builder.Build();
```

---

## Аргументы

```csharp
using Sa.Configuration.CommandLine;

// some.exe --config_db /share/data.db --debug --port -5
var args = new Arguments(args);

string? configDb = args["config_db"];       // → "/share/data.db"
bool?   debug    = args.GetBool("debug");   // → true
int?    port     = args.GetInt("port");     // → -5
TimeSpan? timeout = args.GetTimeSpan("timeout");
```

Поддерживаемые форматы:

```
--key value
--key=value
-key value
-key=value
-flag            → flag=true (булев флаг)
--key -5         → значение "-5" (отрицательные числа никогда не считаются флагами)
```

Типизированные методы возвращают `null`, когда параметр отсутствует или невалиден:

| Метод | Возвращаемый тип | Преобразование |
|-------|-----------------|----------------|
| `GetBool()` | `bool?` | `"true"/"1"/"yes"/"on"` → `true` |
| `GetInt()` | `int?` | `int.TryParse(..., InvariantCulture)` |
| `GetFloat()` | `float?` | то же самое |
| `GetLong()` | `long?` | то же самое |
| `GetTimeSpan()` | `TimeSpan?` | `TimeSpan.TryParse(..., InvariantCulture)` |

`args.Contains("key")` проверяет наличие параметра; `args.Parameters` даёт доступ к полному словарю разобранных параметров.

---

## Секреты

Используйте `Secrets` напрямую, когда нужны секреты вне `IConfiguration`:

```csharp
using Sa.Configuration.SecretStore;

// Цепочка по умолчанию (LIFO): CLI-аргументы → ENV-переменные → secrets.{Env}.txt → secrets.txt
var secrets = Secrets.CreateDefault();

string? result   = secrets.PopulateSecrets("Server={{host}};Pwd={{sa_pg_password}}");
string? password = secrets.GetSecret("sa_pg_password");
```

Пользовательская цепочка — любое `ISecretStore` (`FileSecretStore`, `EnvironmentVariableSecretStore`, `CommandLineArgsSecretStore`, `InMemorySecretStore`):

```csharp
var secrets = new Secrets(new FileSecretStore("my-secrets.txt"));
secrets.AddStore(new InMemorySecretStore().AddSecret("override_key", "override_value"));
// LIFO: новое хранилище имеет наивысший приоритет
```

`Secrets.CreateDefault(new SecretOptions { FileName = "app-secrets.txt", EnvironmentName = "Staging" })` настраивает имя базового файла и среду. `Secrets.GetEnvironmentName()` определяет её из `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` / `environment`, по умолчанию — `Production`.

---

## Лицензия

MIT
