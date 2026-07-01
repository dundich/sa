# Sa.Configuration

Безопасное управление секретами и парсер командной строки в экосистеме .NET `Microsoft.Extensions.Configuration`. Секреты автоматически подставляются в конфигурацию без ручного кода приложения.

---

## Возможности

- **Автоматическая подстановка секретов**: плейсхолдеры `{{key}}` заменяются реальными значениями из файлов, переменных окружения или аргументов командной строки
- **Защита от циклов**: встроенная защита от бесконечной рекурсии при разрешении плейсхолдеров
- **Опциональные плейсхолдеры**: `{{?key}}` — если секрет не найден, возвращается `null` вместо исключения
- **Цепочка хранилищ**: несколько источников секретов с приоритетным порядком
- **Парсер аргументов**: поддерживает форматы `--key value`, `--key=value`, `-flag`
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

Секреты ищутся в порядке убывания приоритета:

| # | Источник | Пример файла |
|---|----------|-------------|
| 1 | Базовый файл секретов | `secrets.txt` |
| 2 | Файл конкретной среды | `secrets.Development.txt` |
| 3 | Переменные окружения | `SA_PG_PASSWORD=...` |
| 4 | Аргументы командной строки | `--sa_pg_password=...` |

Первый источник, имеющий значение, побеждает. Это позволяет переопределять секреты для каждой среды.

---

## Опциональные плейсхолдеры

Используйте `{{?key}}` вместо `{{key}}`, чтобы избежать ошибки при отсутствии секрета:

```json
{
  "optional_feature": "{{?feature_flag}}"
}
```

Если `feature_flag` не найден ни в одном хранилище, возвращается `null`.

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

## Аргументы — Парсер командной строки

```csharp
using Sa.Configuration.CommandLine;

// some.exe --config_db /share/data.db --debug
var args = new Arguments(args);

string? configDb = args["config_db"];       // → "/share/data.db"
bool?   debug    = args.GetBool("debug");   // → true
int?    port     = args.GetInt("port");     // → null
TimeSpan? timeout = args.GetTimeSpan("timeout");
```

Поддерживаемые форматы:

```
--key value
--key=value
-key value
-key=value
-flag          → flag=true (булев флаг)
```

Типизированные методы возвращают `null`, когда параметр отсутствует или невалиден:

| Метод | Возвращаемый тип | Преобразование |
|-------|-----------------|----------------|
| `GetBool()` | `bool?` | `"true"/"1"/"yes"/"on"` → `true` |
| `GetInt()` | `int?` | `int.TryParse(..., InvariantCulture)` |
| `GetFloat()` | `float?` | то же самое |
| `GetLong()` | `long?` | то же самое |
| `GetTimeSpan()` | `TimeSpan?` | `TimeSpan.TryParse(..., InvariantCulture)` |

Дополнительные методы:

| Метод | Возвращаемый тип | Описание |
|-------|-----------------|---------|
| `Contains(param)` | `bool` | Проверяет наличие параметра |
| `IsPresent(param)` | `bool` | Параметр существует И имеет непустое значение |

---

## Секреты — Управление секретами

### Создание по умолчанию

```csharp
using Sa.Configuration.SecretStore;

// Стандартная цепочка: File → File.Env → EnvVar → CommandLine
var secrets = Secrets.CreateDefault();
```

### Пользовательская цепочка

```csharp
var secrets = new Secrets(
    new FileSecretStore("my-secrets.txt"),
    new EnvironmentVariableSecretStore(),
    new InMemorySecretStore(new Dictionary<string, string?> {
        { "override_key", "override_value" }
    })
);
```

### Добавление на лету

```csharp
secrets.AddStore(new FileSecretStore("additional-secrets.txt"));
```

### Подстановка плейсхолдеров

```csharp
string template = "Server={{host}};Password={{password}}";
string result = secrets.PopulateSecrets(template);
// → "Server=localhost;Password=s3cret!"
```

### Получение одного секрета

```csharp
string? password = secrets.GetSecret("sa_pg_password");
```

### Определение имени среды

```csharp
string env = Secrets.GetEnvironmentName();
// → "Development", "Staging", "Production" и т.д.
```

---

## Публичный API

### Пространство имён `Sa.Configuration`

| Тип | Назначение |
|-----|-----------|
| `Setup.AddSaConfiguration()` | Главная точка входа: подключение аргументов + обработка секретов |

### Пространство имён `Sa.Configuration.CommandLine`

| Тип | Назначение |
|-----|-----------|
| `Arguments` | Парсер аргументов командной строки |
| `Arguments.CreateDefault()` | Создаёт из `Environment.GetCommandLineArgs()` |
| `Setup.AddSaCommandLine()` | Метод-расширение для `IConfigurationBuilder` |

### Пространство имён `Sa.Configuration.SecretStore`

| Тип | Назначение |
|-----|-----------|
| `Secrets` | Основной класс управления секретами, реализует `ISecretService` |
| `Secrets.CreateDefault()` | Стандартная цепочка хранилищ |
| `Secrets.GetEnvironmentName()` | Определяет среду (`DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT`) |
| `SecretOptions` | Опции для `CreateDefault()`: `FileName`, `Args`, `EnvironmentName` |
| `ISecretService` | Интерфейс: `PopulateSecrets()` + `GetSecret()` |
| `ISecretStore` | Интерфейс: `GetSecret(string key)` |
| `Setup.AddSaPostSecretProcessing()` | Метод-расширение: применяет `ISecretService` к конфигу ПОСЛЕ загрузки других источников |

### Хранилища секретов (`Sa.Configuration.SecretStore.Stories`)

| Класс | Описание |
|-------|---------|
| `FileSecretStore` | Загружает `key=value` из текстового файла (пропускает комментарии `#`) |
| `EnvironmentVariableSecretStore` | Читает из `Environment.GetEnvironmentVariable()` |
| `CommandLineArgsSecretStore` | Берёт секреты из `Arguments` |
| `InMemorySecretStore` | Словарь в памяти, fluent `.AddSecret()` |

---

## Как это работает

```
┌──────────────────────────────────────────────────────┐
│ 1. appsettings.json содержит:                        │
│    "connection": "Host={{sa_pg_host}};Password={{...}}"│
├──────────────────────────────────────────────────────┤
│ 2. secrets.txt содержит:                             │
│    sa_pg_host=localhost                              │
│    sa_pg_password=s3cret!                            │
├──────────────────────────────────────────────────────┤
│ 3. AddSaPostSecretProcessing подставляет плейсхолдеры:│
│    IConfiguration["sa:pg:connection"]                │
│    → "Host=localhost;Password=s3cret!;..."           │
└──────────────────────────────────────────────────────┘
```

---

## Лицензия

MIT
