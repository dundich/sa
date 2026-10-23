# Sa.Configuration

Безопасное управление секретами и парсер командной строки в экосистеме .NET `Microsoft.Extensions.Configuration`. Секреты автоматически подставляются в конфигурацию без ручного кода приложения.

---

## Возможности

- **Автоматическая подстановка секретов**: [`Быстрый старт`](#quick-start) — плейсхолдеры `{{key}}` заменяются реальными значениями из файлов, переменных окружения или аргументов командной строки
- **Нормализация значений**: значение секретов обрезаются и с их снимаются обрамляющие кавычки (`'`, `"`, `` ` ``) — см. [Кодировки](#кодировки)
- **Защита от циклов**: вложенные плейсхолдеры разрешаются до глубины 3; циклические ссылки бросают `InvalidOperationException`. Подробнее в [Секреты](#secrets)
- **Опциональные плейсхолдеры**: [`{{?key}}`](#optional-placeholders) — если секрет не найден, вся строка становится `null` вместо исключения
- **Цепочка хранилищ**: [`Secrets`](#secrets) объединяет любые [`ISecretStore`](#secrets) с LIFO-приоритетом (последнее добавленное побеждает); `AddStore` потокобезопасен
- **Парсер аргументов**: [`Arguments`](#arguments) поддерживает форматы `--key value`, `--key=value`, `-flag`; отрицательные числа считаются значениями; обрамляющие кавычки удаляются; `__` в имени параметра читается как разделитель `:`
- **Среды разработки**: автоматическая загрузка [`secrets.{Environment}.txt`](#secret-priority-order) (Development/Staging/Production)
- **Алиасы ключей**: [`Алиасы ключей`](#алиасы-ключей) — C#-путь вроде `sa:scope:api:auth:base_url` может заполняться плоским PHP-ключом `auth_base_url`; маппинг применяется к суммарной конфигурации всех провайдеров
- **Поддержка шифрования**: зашифрованные файлы `secrets_enc*.txt` (AES-256-GCM, PBKDF2-SHA256), сырые и парольные ключи (`base64:`), клиенты/среды, `.env`-ключи и Unicode — см. [Зашифрованные секреты](#зашифрованные-секреты)

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
| 3 | `.env.{env}` / `.env` | `SA_SECRETS_DECRYPTION_KEY=...` |
| 4 | `secrets_enc.{client}.{env}.txt` / `secrets_enc.{client}.txt` | закоммиченные зашифрованные секреты |
| 5 | `secrets_enc.{env}.txt` / `secrets_enc.txt` | зашифрованные значения по умолчанию |
| 6 | `secrets.{env}.txt` | обычный файл конкретной среды |
| 7 | `secrets.txt` | обычный базовый файл |

Первый источник со значением побеждает — командная строка может переопределить всё, переменные окружения переопределяют файлы, и т.д.

`{env}` — точное значение `Secrets.GetEnvironmentName()` (`DOTNET_ENVIRONMENT` → `ASPNETCORE_ENVIRONMENT` → `environment`, иначе `Production`). Для коротких имён вроде `dev` задайте `DOTNET_ENVIRONMENT=dev` — имена обычных и зашифрованных файлов следуют за ним.

---

## Зашифрованные секреты

Зашифрованные файлы можно коммитить: значения шифруются AES-256-GCM, а имена ключей и комментарии остаются читаемыми для диффов. Достаточно создать файлы `secrets_enc*.txt` и задать ключ:

```bash
export SA_SECRETS_DECRYPTION_CLIENT=lsr
export SA_SECRETS_DECRYPTION_KEY=...        # или base64:<32-байтный ключ>
# те же переменные можно положить в .env (сам .env не коммитится)
```

Имена файлов — `secrets_enc.txt`, `secrets_enc.{env}.txt`, `secrets_enc.{client}.txt`, `secrets_enc.{client}.{env}.txt`. Часть `{env}` — точное имя среды (`Development`, `Production` или то, что содержит `DOTNET_ENVIRONMENT`).

### Пример зашифрованного файла

`secrets_enc.lsr.dev.txt` — тот самый `secrets.txt` из [быстрого старта](#2-файл-секретов-secretstxt), зашифрованный для клиента `lsr` и среды `dev`:

```
# saenc: v1
# saenc: kdf=pbkdf2-sha256
# saenc: iterations=600000
# saenc: salt=dFgi+38h+4oDUbhsLEUaXg==
# saenc: client=lsr
# saenc: env=dev
# Postgres
sa_pg_host=ENC1:RkVQFBmvvoUBMLkGSXznSaS9mW0h9MVPltZIz4Oou9Q/PEjWiw==
sa_pg_user=ENC1:pBA9GEfkNcZ8+OxP2KWlsfgzDigAq/NWopcYW4GKH4+Qo5Zr
sa_pg_password=ENC1:8zPuIVo2/qAa9OO9mafijG1OYuq1Ku6i7Hg8QRoG2Q8n25dtvb0P7Z1x

# API ключи
api_key=ENC1:Nzz7pPfq4K6rrAixJWQhN37Inu/n6jL+rkFGpJbKyfqAuOs4Aw==
jwt_secret=ENC1:MHP6RX5G7Xc5v+QaSkdhaxVRDRQg8NfCr4OjJzw0BhNEFFgv
```

Ключи, комментарии и заголовок остаются читаемыми — шифруются только значения. Блок выше действительно сгенерирован `do-secrets.sh` с одноразовой фразой `demo-passphrase-not-a-real-secret`, поэтому `decrypt` / `check` можно сразу проверить на нём.

Тот же подход с «сырым» ключом: без `iterations` / `salt` и с `kdf=none` (`SA_SECRETS_DECRYPTION_KEY=base64:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=`):

```
# saenc: v1
# saenc: kdf=none
# saenc: client=lsr
# saenc: env=prod
sa_pg_password=ENC1:fFodkm/LnlPzC6wIu32wkVAvCbXx6th6qG/So7DsX7MSmOUPpRumfOxb
```

Шифрование и проверка — скриптом из репозитория:

```bash
./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets_enc.lsr.dev.txt
./build-sh/do-secrets.sh decrypt secrets_enc.lsr.dev.txt
./build-sh/do-secrets.sh check
```

Если зашифрованный файл существует, а ключ не найден, приложение падает при старте вместо тихого отката на plaintext. Формат, ключи на клиента и поведение при ошибках — в `SecretStore/Readme.md`.

### Кодировки

Всё сквозным образом в UTF-8; единственный «не-UTF-8» токен в файле — это Base64-payload.

| Аспект | Поведение |
|--------|-----------|
| Кодировка файла | UTF-8. BOM допускается и игнорируется (в первой строке заголовка `# saenc: v1` и в `.env`). Файлы без BOM читаются как UTF-8 — содержимое в CP1251/CP866 нужно пересохранить в UTF-8. |
| Переводы строк | LF, CRLF и CR читаются корректно; `SecretsCipher` / `do-secrets.sh` всегда **пишут** LF (`\n`), поэтому повторное шифрование не ломает диффы. |
| Значение → шифротекст | `UTF8.GetBytes(value)` → AES-256-GCM → `nonce(12) ‖ tag(16) ‖ ciphertext` → стандартный Base64 с выравниванием, одной строкой, без переносов. |
| Префикс значения | `ENC1:` — маркер версии формата 1. Сам он не аутентифицируется, аутентифицируется **имя ключа** (`SAENC1\|<key>`), поэтому blob нельзя перенести на другой ключ. |
| Длина payload | `4 × ceil((28 + число байт UTF-8) / 3)`: 9 ASCII-байт (`localhost`) → 52 символа, `superSecret123` (14 байт) → 56, `Привет` (12 байт) → 56. |
| Unicode | Ключи и значения проходят туда-обратно как UTF-8: `Привет`, `pä£s=word`, эмодзи. Не-ASCII символы занимают больше байт — отсюда более длинный blob. |
| `=` в значениях | Допустим — строка режется по **первому** `=`, поэтому `api_key=abc=123` сохраняется без изменений. |
| Пустые значения | Шифруются и восстанавливаются пустыми (`empty=`), без ошибки. |
| Пробелы | Ключи и значения обрезаются с обеих сторон (` key = value ` → `key=value`); внутренние пробелы сохраняются. |
| Комментарии | Строка, которая после trim начинается с `#`, копируется дословно. Комментарий `# saenc:` в **исходном** файле отвергается как «уже зашифрованный». |
| Метаданные заголовка | `client` / `env` обрезаются, не должны содержать переводов строк и сверяются с именем файла при загрузке. |
| Парольная фраза | Любой текст в UTF-8, обрезается — пробелы и не-ASCII допустимы. |
| «Сырой» ключ | `base64:` + Base64 ровно 32 байт (44 символа с выравниванием). Другая длина → ошибка `32 bytes`. |
| Salt | Base64 16 случайных байт (24 символа) в строке `# saenc: salt=`; проверяется длина, поэтому правка salt руками ломает расшифровку. |
| Регистр | Имена ключей, `ENC1:`, `base64:` и имена полей заголовка — ordinal (регистрозависимы): `# saenc: kdf=PBKDF2-SHA256` не принимается. |
| Имена плейсхолдеров | `{{key}}` соответствует `\w+` (Unicode-буквы, цифры, подчёркивание). Дефисы, точки и пробелы не матчатся — `{{api-key}}` остаётся как есть. |
| Подставляемые значения | Обрезаются и освобождаются от обрамляющих кавычек `'`, `"`, `` ` `` перед вставкой в строку конфигурации. |

У `.env` (в нём хранится ключ расшифровки) свои правила кодирования: `KEY=VALUE`, `export KEY=VALUE`, комментарии `#`, необязательный UTF-8 BOM, CRLF/LF, обрамляющие кавычки `'`, `"` или `` ` ``, а также экранирование `\n`, `\r`, `\t`, `\"`, `\\` внутри двойных кавычек. Неизвестные escape-последовательности вроде Windows-пути `"C:\Users"` сохраняют обратный слэш.

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

## Алиасы ключей

Иногда настройка хранится под плоским PHP-ключом, хотя C#-код читает её как вложенный путь.
Например, C#-путь `sa:scope:api:auth:base_url` соответствует плоскому PHP-ключу `auth_base_url`.
Маппинг задаётся в `SecretOptions.KeyAliases` (это `KeyAliasOptions`):

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

Алиас срабатывает на финальном post-secrets-этапе, **к суммарной конфигурации всех
провайдеров** — поэтому плоский ключ и канонический путь могут жить в разных источниках
(например, плоский ключ — в PostgreSQL-источнике, а канонический путь — в `appsettings.json`).
Читая результат, обе записи дают одно значение:

```csharp
string? baseUrl = app.Configuration["sa:scope:api:auth:base_url"]; // → "https://php.example.com"
string? flat    = app.Configuration["auth_base_url"];             // → "https://php.example.com"
```

### Разрешение

- **Непустое** значение алиаса по умолчанию записывается в канонический путь — алиас побеждает
  за канонический ключ.
- `CanonicalValueWins = true` инвертирует это: непустое значение алиаса тогда заполняет
  канонический путь **только когда он пуст или отсутствует** в суммарной конфигурации, то есть
  значение, которое уже заполнил источник с меньшим приоритетом, побеждает над алиасом.

```csharp
builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        CanonicalValueWins = true,   // побеждает каноническое значение; алиас только заполняет пустой путь
    },
});
```

- **Отсутствующее или пустое** значение алиаса никогда не трогает канонический путь — тот
  сохраняет своё значение (если есть) или остаётся отсутствующим, и за него отвечает источник
  с меньшим приоритетом; никакого поддельного значения не создаётся. Пустое значение всегда
  считается «без значения».
- Плоский алиас-ключ **не** убирается: `auth_base_url` остаётся доступным напрямую, поэтому
  PHP-ключи можно читать, пока C#-код читает канонический путь.

Ключи маппинга сравниваются так же, как ключи конфигурации: без учёта регистра, и в виде `:`
когда содержат `__` (запись разделителя в стиле переменных окружения), поэтому
`sa__scope__api__auth__base_url` в маппинге указывает на тот же ключ, что и
`sa:scope:api:auth:base_url`. Пустой ключ или пустая строка в маппинге — ошибка на этапе
регистрации: такой алиас никогда не совпадёт ни с одной настройкой, поэтому она падает, пока
хост ещё собирается, а не молча ничего не делает. Пустой маппинг оставляет конвейер без
изменений.

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

Когда настройка хранится в базе под плоским PHP-ключом (напр. `auth_base_url`),
вместо вложенного C#-пути (`sa:scope:api:auth:base_url`), задайте маппинг в Secrets-слое —
**не** в опциях PG — чтобы он применялся к суммарной конфигурации всех провайдеров
(см. [Алиасы ключей](#алиасы-ключей)):

```csharp
using Sa.Configuration;

builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
    },
});

string? baseUrl = app.Configuration["sa:scope:api:auth:base_url"];
// → значение строки auth_base_url
```

Плоский ключ остаётся доступным как собственный ключ (`app.Configuration["auth_base_url"]`).

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

> **`__` как `:`** — двойное подчёркивание в имени параметра — это запись разделителя `:` в стиле
> переменных окружения, поэтому `--sa__pg__host` хранится и читается как `sa:pg:host` (одиночные
> подчёркивания — данные). `args["sa__pg__host"]` и `args["sa:pg:host"]` взаимозаменяемы, и
> типизированные геттеры следуют тому же правилу.

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

// Цепочка по умолчанию (LIFO): CLI-аргументы → ENV-переменные → .env → secrets_enc* → secrets.{Env}.txt → secrets.txt
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
