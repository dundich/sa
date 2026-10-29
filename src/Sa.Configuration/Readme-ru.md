# Sa.Configuration

Пакет для .NET, который делает две вещи:

1. **Подставляет секреты в конфигурацию** — вместо того чтобы хардкодить пароли и ключи, вы пишете в конфиге `{{sa_pg_password}}`, а пакет сам подставляет настоящее значение из файла, переменной окружения или аргумента командной строки.
2. **Разбирает аргументы командной строки** — `--key value`, `--key=value`, `-flag` и т.д.

Работает поверх стандартного `Microsoft.Extensions.Configuration`, так что встраивается в обычный ASP.NET-конвейер.

---

## Что умеет

- **Автоподстановка секретов** — плейсхолдеры `{{key}}` заменяются реальными значениями. См. [Быстрый старт](#быстрый-старт).
- **Чистка значений** — у значений обрезаются пробелы по краям и снимаются кавычки (`'`, `"`, `` ` ``). Подробнее — в разделе [Кодировки](#кодировки).
- **Защита от зацикливания** — вложенные плейсхолдеры раскрываются максимум на 3 уровня. Если получился цикл — понятная ошибка `InvalidOperationException`. Подробнее — в разделе [Секреты](#секреты).
- **Необязательные плейсхолдеры** — `{{?key}}` вместо `{{key}}`: если секрет не найден, строка станет `null`, а не упадёт с ошибкой.
- **Несколько хранилищ сразу** — [`Secrets`](#секреты) умеет объединять любое количество хранилищ (`ISecretStore`). Побеждает то, что добавлено последним. Добавлять хранилища можно на ходу, в том числе из разных потоков.
- **Разбор аргументов** — [`Arguments`](#аргументы) понимает `--key value`, `--key=value`, `-flag`. Отрицательные числа считаются значениями, а не флагами. Кавычки снимаются. `__` в имени параметра читается как разделитель `:`.
- **Среды** — автоматически подхватывает `secrets.{Environment}.txt` для Development/Staging/Production.
- **Алиасы ключей** — [Алиасы ключей](#алиасы-ключей): можно связать «плоское» имя настройки (например, `auth_base_url` из PHP-мира) с вложенным C#-путём (`sa:scope:api:auth:base_url`). Работает поверх всех источников сразу.
- **Шифрование** — зашифрованные файлы `secrets*.pub` (AES-256-GCM) можно коммитить в репозиторий. Ключи шифрования — обычные или `base64:`. Поддерживаются клиенты, среды, `.env`, Unicode. См. [Зашифрованные секреты](#зашифрованные-секреты).

---

## Быстрый старт

### 1. Подключаем пакет в `Program.cs`

```csharp
using Sa.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Подключаем аргументы + секреты из файлов/ENV/CLI
builder.Configuration.AddSaConfiguration();

var app = builder.Build();
```

### 2. Создаём файл секретов `secrets.txt`

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

> ⚠️ Не забудьте добавить `secrets*.txt` в `.gitignore`!

### 3. Пишем плейсхолдеры в `appsettings.json`

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

### 4. Читаем конфигурацию как обычно

```csharp
var pgConn = app.Configuration["sa:pg:connection"];
// → "User ID=postgres;Password=superSecret123;Host=localhost;..."
```

---

## Откуда берутся секреты (порядок приоритета)

Источники проверяются по порядку. Побеждает первый, где значение нашлось. Чем выше в списке — тем важнее:

| # | Источник | Пример |
|---|----------|--------|
| 1 | Аргументы командной строки | `--sa_pg_password=...` |
| 2 | Переменные окружения | `SA_PG_PASSWORD=...` |
| 3 | `.env.{env}` / `.env` | `SA_SECRETS_DECRYPTION_KEY=...` |
| 4 | `secrets.{client}.{env}.pub` / `secrets.{client}.pub` | зашифрованные секреты, закоммиченные в репо |
| 5 | `secrets.{env}.pub` / `secrets.pub` | зашифрованные значения по умолчанию |
| 6 | `secrets.{env}.txt` | обычный файл для конкретной среды |
| 7 | `secrets.txt` | обычный базовый файл |

Проще говоря: командная строка перекрывает всё, переменные окружения перекрывают файлы, файлы для среды перекрывают базовый файл.

Что такое `{env}` — это имя среды, которое берётся из `DOTNET_ENVIRONMENT` → `ASPNETCORE_ENVIRONMENT` → `environment`. Если нигде не задано — `Production`. Хотите короткое имя вроде `dev` — задайте `DOTNET_ENVIRONMENT=dev`, и имена всех файлов будут следовать за ним.

---

## Зашифрованные секреты

Зашифрованные файлы можно спокойно коммитить в репозиторий: значения зашифрованы AES-256-GCM, а имена ключей и комментарии остаются читаемыми — удобно смотреть диффы.

Как пользоваться:

1. Создайте файлы `secrets*.pub`.
2. Задайте ключ расшифровки:

```bash
export SA_SECRETS_DECRYPTION_CLIENT=lsr
export SA_SECRETS_DECRYPTION_KEY=...        # или base64:<32-байтный ключ>
# те же переменные можно положить в .env (сам .env в репо не коммитится)
```

Возможные имена файлов:

- `secrets.pub`
- `secrets.{env}.pub`
- `secrets.{client}.pub`
- `secrets.{client}.{env}.pub`

`{env}` — точное имя среды (`Development`, `Production` или то, что лежит в `DOTNET_ENVIRONMENT`).

Расширение `.pub` означает файл, который безопасно коммитить; внутри — шифртекст AES-256-GCM, а не публичный ключ.

### Пример зашифрованного файла

Вот тот же `secrets.txt` из [быстрого старта](#2-создаём-файл-секретов-secretstxt), но зашифрованный для клиента `lsr` и среды `dev`:

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

Что осталось читаемым: ключи, комментарии, заголовок. Что зашифровано: только значения.

Этот блок реально сгенерирован скриптом `do-secrets.sh` с одноразовой фразой `demo-passphrase-not-a-real-secret` — так что команды `decrypt` и `check` можно проверить прямо на нём.

Тот же подход, но с «сырым» ключом (без `iterations` / `salt`, с `kdf=none`, ключ `SA_SECRETS_DECRYPTION_KEY=base64:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=`):

```
# saenc: v1
# saenc: kdf=none
# saenc: client=lsr
# saenc: env=prod
sa_pg_password=ENC1:fFodkm/LnlPzC6wIu32wkVAvCbXx6th6qG/So7DsX7MSmOUPpRumfOxb
```

Скрипты для шифрования и проверки лежат в репозитории:

```bash
./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets.lsr.dev.pub
./build-sh/do-secrets.sh decrypt secrets.lsr.dev.pub
./build-sh/do-secrets.sh check
```

Важное поведение: если зашифрованный файл есть, а ключ не найден — приложение **падает при старте**. Это сделано специально, чтобы не откатиться молча на незашифрованный plaintext.

Формат, отдельные ключи на клиента и обработка ошибок описаны в `SecretStore/Readme-ru.md`.

### Кодировки

Всё работает в UTF-8. Единственный «не-UTF-8» токен в файле — это Base64-payload (шифротекст).

| Что | Как работает |
|-----|--------------|
| Кодировка файла | UTF-8. BOM допускается и игнорируется. Файлы без BOM читаются как UTF-8 — если у вас CP1251/CP866, пересохраните в UTF-8. |
| Переводы строк | LF, CRLF и CR читаются корректно. При записи (`SecretsCipher` / `do-secrets.sh`) всегда используется LF, поэтому повторное шифрование не ломает диффы. |
| Как значение превращается в шифротекст | UTF-8 байты → AES-256-GCM → `nonce(12) ‖ tag(16) ‖ ciphertext` → стандартный Base64 одной строкой. |
| Префикс значения | `ENC1:` — маркер версии формата 1. Сам он не аутентифицируется; аутентифицируется имя ключа, поэтому blob нельзя «перенести» на другой ключ. |
| Длина payload | `4 × ceil((28 + число байт UTF-8) / 3)`. Примеры: `localhost` (9 байт) → 52 символа, `superSecret123` (14 байт) → 56, `Привет` (12 байт) → 56. |
| Unicode | Ключи и значения проходят туда-обратно как UTF-8: `Привет`, `pä£s=word`, эмодзи. Не-ASCII занимает больше байт — поэтому blob длиннее. |
| `=` внутри значения | Разрешён. Строка режется по первому `=`, поэтому `api_key=abc=123` сохраняется целиком. |
| Пустые значения | Шифруются и восстанавливаются пустыми (`empty=`), без ошибки. |
| Пробелы | У ключей и значений обрезаются по краям (` key = value ` → `key=value`). Внутренние пробелы сохраняются. |
| Комментарии | Строка, которая после trim начинается с `#`, копируется дословно. Комментарий `# saenc:` в исходном файле отвергается как «уже зашифрованный». |
| Метаданные заголовка | `client` / `env` обрезаются, не должны содержать переводов строк и сверяются с именем файла при загрузке. |
| Парольная фраза | Любой текст в UTF-8, обрезается. Пробелы и не-ASCII допустимы. |
| «Сырой» ключ | `base64:` + Base64 ровно 32 байт (44 символа с выравниванием). Другая длина → ошибка `32 bytes`. |
| Salt | Base64 16 случайных байт (24 символа) в строке `# saenc: salt=`. Длина проверяется, поэтому правка salt руками ломает расшифровку. |
| Регистр | Имена ключей, `ENC1:`, `base64:` и поля заголовка — регистрозависимы. `# saenc: kdf=PBKDF2-SHA256` не принимается. |
| Имена плейсхолдеров | `{{key}}` соответствует `\w+` (Unicode-буквы, цифры, подчёркивание). Дефисы, точки и пробелы не матчатся — `{{api-key}}` остаётся как есть. |
| Что подставляется | Значение обрезается и освобождается от кавычек `'`, `"`, `` ` `` перед вставкой в строку конфигурации. |

У `.env` (там хранится ключ расшифровки) свои правила: `KEY=VALUE`, `export KEY=VALUE`, комментарии `#`, необязательный UTF-8 BOM, CRLF/LF, кавычки `'`, `"`, `` ` ``, а также экранирование `\n`, `\r`, `\t`, `\"`, `\\` внутри двойных кавычек. Неизвестные escape-последовательности вроде Windows-пути `"C:\Users"` сохраняют обратный слэш.

---

## Необязательные плейсхолдеры

Обычно если секрет не найден — это ошибка. Но иногда настройка нужна «если есть». Тогда пишем `{{?key}}`:

```json
{
  "optional_feature": "{{?feature_flag}}"
}
```

Если `feature_flag` не найден ни в одном хранилище — вся строка становится `null`. Никаких исключений.

Кстати, в цепочке конфигурации (`AddSaConfiguration` / `AddSaPostSecretProcessing`) **любой** отсутствующий секрет — даже обычный `{{key}}` — даёт `null` вместо исключения. Так что поведение там мягче, чем при прямом вызове `PopulateSecrets`.

---

## Алиасы ключей

### В чём проблема

Одна и та же настройка может называться по-разному в разных системах:

- В PHP (или в базе, куда пишет PHP) — плоский ключ: `auth_base_url`.
- В C# удобнее вложенный путь: `sa:scope:api:auth:base_url`.

Переписывать конфиг или код ради этого не хочется. Алиас связывает два имени: «когда кто-то спрашивает путь — посмотри плоский ключ».

### Как задать

```csharp
using Sa.Configuration;

builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases =
        {
            ["sa:scope:api:auth:base_url"] = "auth_base_url",   // путь → плоский ключ
            ["sa:scope:api:auth:token_ttl"] = "auth_token_ttl",
        },
    },
});
```

Слева — путь, который читает C#. Справа — имя, под которым значение реально лежит.

### Несколько регистраций (multisetup)

Вызывать `AddSaConfiguration` несколько раз можно — вызовы складываются:

```csharp
builder.Configuration
    .AddSaConfiguration(new SecretOptions { KeyAliases = { ["sa:…:base_url"] = "auth_base_url" } })
    .AddSaConfiguration(new SecretOptions { KeyAliases = { ["sa:…:token_ttl"] = "auth_token_ttl" } });
```

- каждый вызов **дописывает** свои `KeyAliases` в одну общую карту; если канонический ключ объявлен дважды, побеждает **последний** вызов;
- опции секретов (`FileName`, `LoadDotEnv`, `Encryption`, `Args`) тоже берутся из **последнего** вызова;
- регистрируется **один** проход и **один** источник командной строки, поэтому секреты читаются один раз — при первом `Build()` (сама регистрация файлы не трогает);
- `CanonicalValueWins` — один флаг на всю карту, его значение тоже берётся из последнего вызова.

### Алиас должен быть последним

Проход записывает производные значения и поэтому побеждает все источники, зарегистрированные **до** него. Источник, добавленный **после** последнего `AddSaConfiguration`, — обычный провайдер: для своих ключей он перекрывает алиас. Если источники регистрируются позже, последним шагом вызывайте `FinalizeSaConfiguration()`:

```csharp
builder.Configuration
    .AddSaPostgreSqlConfiguration(...)   // источник с каноническими ключами
    .AddSaConfiguration(...)             // алиасы
    .FinalizeSaConfiguration();          // проход уходит в конец
```

### Свои трансформы

Финальный проход расширяется: `AddSaConfigurationTransform` добавляет шаг, который получает суммарную конфигурацию и возвращает **только** те ключи, которые меняет. Трансформы выполняются после встроенных (плейсхолдеры, затем алиасы) в порядке регистрации, и каждая видит результат предыдущих:

```csharp
public sealed class StripPrefixTransform(string prefix) : ISaConfigurationTransform
{
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
        => configuration.Values
            .Where(c => c.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(c => new KeyValuePair<string, string?>(c.Key[prefix.Length..], c.Value));
}

builder.Configuration.AddSaConfigurationTransform(new StripPrefixTransform("legacy:"));
```

### Что происходит после этого

Оба имени отдают одно значение:

```csharp
app.Configuration["sa:scope:api:auth:base_url"]; // → "https://php.example.com"
app.Configuration["auth_base_url"];               // → "https://php.example.com"
```

Плоский ключ никуда не исчезает — он остаётся доступным. PHP-код работает как раньше, C#-код получает свой вложенный путь.

### Где ищется значение

Алиас применяется к **итоговой** конфигурации — той, что уже собрана из **всех** источников (файлы, ENV, PostgreSQL и т.д.). Поэтому плоский ключ и канонический путь могут лежать **в разных источниках**:

- `auth_base_url` — в PostgreSQL-источнике.
- `sa:scope:api:auth:base_url` — в `appsettings.json`.

Алиас их склеит, и оба пути вернут одно значение.

### Кто кого перекрывает

- **По умолчанию побеждает алиас.** Непустое значение из плоского ключа записывается в канонический путь.
- **С `CanonicalValueWins = true`** — наоборот: каноническое значение имеет приоритет. Алиас заполняет путь, только если тот пуст.

```csharp
builder.Configuration.AddSaConfiguration(new SecretOptions
{
    KeyAliases = new KeyAliasOptions
    {
        KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        CanonicalValueWins = true,   // каноническое значение побеждает
    },
});
```

- **Если алиас пуст или отсутствует** — канонический путь не трогается. Он сохраняет своё значение (если есть) или остаётся отсутствующим, и тогда за него отвечает источник с меньшим приоритетом. Никаких «пустышек» не подставляется.

### Мелочи

- **Регистр не важен**: `Auth_Base_Url` и `auth_base_url` — одно и то же.
- **`__` = `:`**: в маппинге можно писать `sa__scope__api__auth__base_url` — это то же самое, что `sa:scope:api:auth:base_url`.
- **Пустой ключ или пустая строка в маппинге** — ошибка при старте. Такой алиас никогда ни с чем не совпадёт, поэтому лучше упасть сразу, чем молча не работать.
- **Пустой маппинг** — ничего не меняется, конвейер работает как обычно.
- **`Reload()`** — проход пересчитывается: ключ, исчезнувший из источника или из маппинга, перестаёт отдаваться.

### Шпаргалка

| Хочу | Настройка |
|------|-----------|
| Связать плоский ключ с вложенным путём | `KeyAliases["путь"] = "плоский_ключ"` |
| Плоский ключ побеждает | по умолчанию |
| Вложенный путь побеждает | `CanonicalValueWins = true` |
| Значения нет | путь не трогается, работает lower-priority источник |

---

## Использование вместе с Sa.Configuration.PostgreSql

Сначала подключаем источники, затем Sa-конвейер, и финализируем его — так алиасы ложатся поверх всех источников, включая динамические настройки из БД:

```csharp
using Sa.Configuration;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Стандартные источники (appsettings.json, secrets.txt)
builder.Configuration.AddJsonFile("appsettings.json");

// Динамические настройки из базы данных
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    ConnectionString: "...",
    SelectSql: "SELECT key, value FROM app_settings"
));

// Финальный проход: плейсхолдеры секретов + алиасы ключей
builder.Configuration
    .AddSaConfiguration()
    .FinalizeSaConfiguration();

var app = builder.Build();
```

Если в базе настройка лежит под плоским PHP-ключом (например, `auth_base_url`), а C#-код читает вложенный путь (`sa:scope:api:auth:base_url`), задайте маппинг **в Secrets-слое**, а не в опциях PG. Тогда он применится ко всем провайдерам сразу:

```csharp
using Sa.Configuration;

builder.Configuration
    .AddSaConfiguration(new SecretOptions
    {
        KeyAliases = new KeyAliasOptions
        {
            KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        },
    })
    .FinalizeSaConfiguration();

string? baseUrl = app.Configuration["sa:scope:api:auth:base_url"];
// → значение строки auth_base_url
```

Плоский ключ при этом остаётся доступным отдельно: `app.Configuration["auth_base_url"]`.

---

## Аргументы командной строки

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

> **`__` как `:`** — двойное подчёркивание в имени параметра — это способ записать разделитель `:` в стиле переменных окружения. Поэтому `--sa__pg__host` хранится и читается как `sa:pg:host`. Одиночные подчёркивания — это данные, не разделители. `args["sa__pg__host"]` и `args["sa:pg:host"]` взаимозаменяемы, и типизированные геттеры работают так же.

Типизированные методы возвращают `null`, если параметр отсутствует или невалиден:

| Метод | Тип | Как преобразует |
|-------|-----|-----------------|
| `GetBool()` | `bool?` | `"true"/"1"/"yes"/"on"` → `true` |
| `GetInt()` | `int?` | `int.TryParse(..., InvariantCulture)` |
| `GetFloat()` | `float?` | то же самое |
| `GetLong()` | `long?` | то же самое |
| `GetTimeSpan()` | `TimeSpan?` | `TimeSpan.TryParse(..., InvariantCulture)` |

Проверить наличие параметра — `args.Contains("key")`. Полный словарь — `args.Parameters`.

---

## Работа с секретами напрямую

Иногда секреты нужны вне `IConfiguration`. Тогда используйте `Secrets` напрямую:

```csharp
using Sa.Configuration.SecretStore;

// Цепочка по умолчанию (сверху вниз приоритет):
// CLI-аргументы → ENV-переменные → .env → secrets*.pub → secrets.{Env}.txt → secrets.txt
var secrets = Secrets.CreateDefault();

string? result   = secrets.PopulateSecrets("Server={{host}};Pwd={{sa_pg_password}}");
string? password = secrets.GetSecret("sa_pg_password");
```

Своя цепочка — любое хранилище `ISecretStore` (`FileSecretStore`, `EnvironmentVariableSecretStore`, `CommandLineArgsSecretStore`, `InMemorySecretStore`):

```csharp
var secrets = new Secrets(new FileSecretStore("my-secrets.txt"));
secrets.AddStore(new InMemorySecretStore().AddSecret("override_key", "override_value"));
// LIFO: новое хранилище имеет наивысший приоритет
```

Можно настроить имя базового файла и среду:

```csharp
Secrets.CreateDefault(new SecretOptions
{
    FileName = "app-secrets.txt",
    EnvironmentName = "Staging"
});
```

Имя среды `Secrets.GetEnvironmentName()` берёт из `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` / `environment`, по умолчанию — `Production`.

---

## Лицензия

MIT
