# Sa.Configuration

**Русский** | [English](Readme.md)

Пакет для .NET, который собирает и расширяет конфигурацию:

1. **Собирает стек слоёв** — статические источники (appsettings, переменные окружения, аргументы, секреты) и динамические (БД) в одном детерминированном порядке, с builder'ом под две стратегии.
2. **Подставляет секреты в конфигурацию** — вместо того чтобы хардкодить пароли и ключи, вы пишете в конфиге `{{sa_pg_password}}`, а пакет сам подставляет настоящее значение из файла, переменной окружения или аргумента командной строки.
3. **Накладывает клиентские настройки** — значения `sa:clients:{client}:{key}` ложатся поверх общих, а конфигурация каждого клиента доступна как keyed `IConfiguration`.
4. **Перезагружает только динамический слой** — `ISaConfigurationReloader` обновляет источники БД (и производные значения) без перечитывания файлов, и не регистрирует фоновую службу, пока её не попросят.
5. **Разбирает аргументы командной строки** — `--key value`, `--key=value`, `-flag` и т.д.

Работает поверх стандартного `Microsoft.Extensions.Configuration`, так что встраивается в обычный ASP.NET-конвейер.

---

## Содержание

- [Что умеет](#что-умеет)
- [Слои и builder](#слои-и-builder)
- [Быстрый старт](#быстрый-старт)
  - [1. Подключаем пакет в `Program.cs`](#1-подключаем-пакет-в-programcs)
  - [2. Создаём файл секретов `secrets.txt`](#2-создаём-файл-секретов-secretstxt)
  - [3. Пишем плейсхолдеры в `appsettings.json`](#3-пишем-плейсхолдеры-в-appsettingsjson)
  - [4. Читаем конфигурацию как обычно](#4-читаем-конфигурацию-как-обычно)
- [Откуда берутся секреты (порядок приоритета)](#откуда-берутся-секреты-порядок-приоритета)
- [Зашифрованные секреты](#зашифрованные-секреты)
  - [Пример зашифрованного файла](#пример-зашифрованного-файла)
  - [Кодировки](#кодировки)
- [Необязательные плейсхолдеры](#необязательные-плейсхолдеры)
- [Алиасы ключей](#алиасы-ключей)
  - [В чём проблема](#в-чём-проблема)
  - [Как задать](#как-задать)
  - [Несколько регистраций (multisetup)](#несколько-регистраций-multisetup)
  - [Алиас должен быть последним](#алиас-должен-быть-последним)
  - [Свои трансформы](#свои-трансформы)
  - [Что происходит после этого](#что-происходит-после-этого)
  - [Где ищется значение](#где-ищется-значение)
  - [Кто кого перекрывает](#кто-кого-перекрывает)
  - [Мелочи](#мелочи)
  - [Шпаргалка](#шпаргалка)
- [Совместная работа с Sa.Configuration.PostgreSql](#совместная-работа-с-saconfigurationpostgresql)
- [Клиенты (keyed-конфигурация)](#клиенты-keyed-конфигурация)
- [Обновление динамического слоя](#обновление-динамического-слоя)
- [Полный пример DI](#полный-пример-di)
- [Аргументы командной строки](#аргументы-командной-строки)
- [Работа с секретами напрямую](#работа-с-секретами-напрямую)
- [Лицензия](#лицензия)

## Что умеет

- **Автоподстановка секретов** — плейсхолдеры `{{key}}` заменяются реальными значениями. См. [Быстрый старт](#быстрый-старт).
- **Чистка значений** — у значений обрезаются пробелы по краям и снимаются кавычки (`'`, `"`, `` ` ``). Подробнее — в разделе [Кодировки](#кодировки).
- **Защита от зацикливания** — вложенные плейсхолдеры раскрываются максимум на 3 уровня. Если получился цикл — понятная ошибка `InvalidOperationException`. Подробнее — в разделе [Секреты](SecretStore/Readme-ru.md).
- **Необязательные плейсхолдеры** — `{{?key}}` вместо `{{key}}`: если секрет не найден, строка станет `null`, а не упадёт с ошибкой.
- **Несколько хранилищ сразу** — [`Secrets`](SecretStore/Readme-ru.md) умеет объединять любое количество хранилищ (`ISecretStore`). Побеждает то, что добавлено последним. Добавлять хранилища можно на ходу, в том числе из разных потоков.
- **Разбор аргументов** — [`Arguments`](CommandLine/Readme-ru.md) понимает `--key value`, `--key=value`, `-flag`. Отрицательные числа считаются значениями, а не флагами. Кавычки снимаются. `__` в имени параметра читается как разделитель `:`.
- **Среды** — автоматически подхватывает `secrets.{Environment}.txt` для Development/Staging/Production.
- **Алиасы ключей** — [Алиасы ключей](#алиасы-ключей): можно связать «плоское» имя настройки (например, `auth_base_url` из PHP-мира) с вложенным C#-путём (`sa:scope:api:auth:base_url`). Работает поверх всех источников сразу.
- **Шифрование** — зашифрованные файлы `secrets*.pub` (AES-256-GCM) можно коммитить в репозиторий. Ключи шифрования — обычные или `base64:`. Поддерживаются клиенты, среды, `.env`, Unicode. См. [Зашифрованные секреты](#зашифрованные-секреты).
- **Builder слоёв** — `AddSaConfiguration(sa => ...)` собирает статические и динамические источники в фиксированном порядке. См. [Слои и builder](#слои-и-builder).
- **Клиентский оверлей** — `sa:clients:{client}:{key}` в любом источнике, keyed `IConfiguration` на клиента и фабрика для имён, известных только в рантайме. См. [Клиенты](#клиенты-keyed-конфигурация).
- **Обновление динамики** — перечитываются только источники БД, а алиасы и плейсхолдеры пересчитываются от свежих значений. По умолчанию без таймера. См. [Обновление динамического слоя](#обновление-динамического-слоя).

---

## Слои и builder

Конфигурация собирается из источников двух видов:

| Слой | Типичные источники | Как меняется в рантайме |
|------|--------------------|-------------------------|
| **Static** | `appsettings.json`, `appsettings.{environment}.json`, переменные окружения, аргументы, `secrets*.txt` | правка файла (`reloadOnChange`), явный `IConfigurationRoot.Reload()` или рестарт |
| **Dynamic** | PostgreSQL или любой `ISaDynamicConfigurationSource` | `ISaConfigurationReloader.ReloadDynamic[Async]()` — и перечитывается **только** этот слой |

`AddSaConfiguration(sa => ...)` — это builder. Правило одно: **источник, добавленный позже, перекрывает предыдущий**, а pass секретов/алиасов всегда остаётся после всех источников, добавленных через него.

```csharp
// Стратегия 1 — только статика: файлы, окружение, аргументы, секреты.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseStandardSources();   // appsettings.json → appsettings.{env}.json → env → args
    sa.UseSecrets();           // {{placeholder}} + алиасы ключей
});
```

```csharp
// Стратегия 2 — статика + динамика + клиенты.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseStandardSources();                                                // статика
    sa.UseSecrets();                                                        // {{…}} + алиасы
    sa.UseDynamic(new DatabaseConfigurationSource(                          // динамика, перекрывает статику
        new PostgreSqlConfigurationOptions("SELECT client_id, key, value FROM settings")
        {
            ClientColumn = "client_id",
        },
        PgConfigurationConnection.FromServices()));
    sa.UseClients();                                                        // sa:clients:{client}:{key}
});
```

| Метод | Что добавляет |
|-------|---------------|
| `UseStandardSources(...)` | `appsettings.json`, `appsettings.{env}.json`, переменные окружения, аргументы. **Идемпотентен**: в ASP.NET Core / generic host хост уже добавил их, и метод ничего не делает. |
| `UseSecrets(...)` | прежний конвейер секретов: плейсхолдеры, алиасы, источник аргументов. |
| `UseDynamic(source)` | источник, значения которого меняются в рантайме; ставится после статических. |
| `UseClients(path = "sa:clients")` | клиентский оверлей, включая разрешение алиасов внутри каждого клиентского префикса. |
| `Use(source)` | любой `IConfigurationSource` — для источников без отдельного метода. |
| `Finalize()` | переносит pass в конец (нужно, только если источники добавляются напрямую в `sa.Builder`). |

Источник, зарегистрированный после последнего вызова Sa, — обычный провайдер и выигрывает у pass'а для своих ключей; `Finalize()` (или `sa.Builder.Add(...)`, затем `Finalize()`) — явный способ вернуть порядок.

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

## Совместная работа с Sa.Configuration.PostgreSql

Пакет PostgreSQL добавляет динамический источник. Ему нужна стратегия подключения, а pass Sa
остаётся после него — `AddSaPostgreSqlConfiguration` уже это делает:

```csharp
using Sa.Configuration;
using Sa.Configuration.PostgreSql;

// Пул приложения (или NpgsqlDataSource): ничего лишнего не создаётся.
builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));

// Либо явный пул, либо строка подключения, которой владеет сам источник:
builder.Configuration.AddSaPostgreSqlConfiguration(
    appDataSource, new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
builder.Configuration.AddSaPostgreSqlConfiguration(
    "Host=db;Database=app;Username=app;Password=…",
    new PostgreSqlConfigurationOptions("SELECT key, value FROM app_settings"));
```

Алиасы из слоя секретов применяются поверх значений из БД, а слой БД перекрывает статику (он
зарегистрирован позже). Формы запросов, `ClientColumn` и детали обновления — в README пакета.

---

## Клиенты (keyed-конфигурация)

Базовые значения общие, клиент перекрывает только то, что ему нужно. Конвенция — один префикс
ключа `sa:clients:{client}:{key}`, поэтому данные клиента могут лежать в **любом** источнике:
JSON, переменные окружения, аргументы командной строки или БД. Ни отдельного провайдера, ни
второго пула.

```text
theme                          ← базовое значение, если у клиента нет переопределения
sa:clients:acme:theme          ← переопределение для "acme"
sa:clients:beta:theme          ← переопределение для "beta"
```

### Статика: несколько клиентов в `appsettings.json`

```json
{
  "theme": "dark",
  "language": "en",
  "sa": {
    "clients": {
      "acme": { "theme": "light" },
      "beta": { "language": "de", "theme": "dark" }
    }
  }
}
```

Те же данные через другие статические источники:

```bash
# переменные окружения: __ — это разделитель ":"
export SA__CLIENTS__ACME__THEME=light

# командная строка
./app --sa__clients__beta__language=de
```

### Динамика: SQL, который возвращает всех клиентов сразу

```sql
-- Один запрос, один пул, все клиенты. client_id IS NULL — базовые значения.
SELECT client_id, key, value FROM settings;
```

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.FromServices(),
    new PostgreSqlConfigurationOptions("SELECT client_id, key, value FROM settings")
    {
        ClientColumn = "client_id",                 // первая колонка: client, key, value
        ClientsSection = "sa:clients",              // значение по умолчанию, показано явно
    });
```

### Клиент без отдельной колонки: вычисляем из ключа

Физической колонки клиента может не быть вовсе — например, в старой таблице клиент просто
вклеен в ключ конкатенацией (`acme.theme` — «клиент» + `.` + «настройка»). `ClientColumn`
работает и так: клиента достаточно вычислить в запросе и вернуть **первой** колонкой. Строки
без разделителя остаются базовыми значениями — для них клиент `NULL`.

```sql
-- Ключ — конкатенация «клиент.настройка»; отдельной колонки client_id в таблице нет.
-- Строка без точки → базовая: client NULL, key — вся строка целиком.
SELECT CASE WHEN strpos(key, '.') = 0 THEN NULL
            ELSE split_part(key, '.', 1) END          AS client_id,
       CASE WHEN strpos(key, '.') = 0 THEN key
            ELSE substr(key, strpos(key, '.') + 1) END AS key,
       value
FROM legacy_settings;
```

```sql
INSERT INTO legacy_settings (key, value) VALUES
    ('theme',         'dark'),    -- → базовое theme
    ('language',      'en'),      -- → базовое language
    ('acme.theme',    'light'),   -- → sa:clients:acme:theme
    ('beta.language', 'de');      -- → sa:clients:beta:language
```

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.FromServices(),
    new PostgreSqlConfigurationOptions(
        """
        SELECT CASE WHEN strpos(key, '.') = 0 THEN NULL ELSE split_part(key, '.', 1) END AS client_id,
               CASE WHEN strpos(key, '.') = 0 THEN key ELSE substr(key, strpos(key, '.') + 1) END AS key,
               value
        FROM legacy_settings
        """)
    {
        ClientColumn = "client_id",                 // по-прежнему первая колонка
    });
```

Имя `client_id` здесь — просто метка: совпадать с физической колонкой не требуется. Важен только
порядок — клиент, затем `key`, затем `value`.

Обратный случай тоже решается в запросе: если физической колонки `key` нет, а ключ — это
конкатенация нескольких колонок, склейте их в SQL, сохранив форму `client, key, value`:

```sql
SELECT client_id, section || ':' || name AS key, value FROM settings;
```

### Ключи в стиле ENV: `__` как разделитель

Если таблица заполняется из тех же мест, что и переменные окружения, ключи в ней пишутся «плоско» —
с `__` вместо `:`. Тогда ни резать строки в SQL, ни задавать `ClientColumn` не нужно: включите
`DoubleUnderscoreAsSeparator`, и источник прочитает `__` как разделитель ещё до раскладки по
клиентам, поэтому клиентский префикс соберётся сам.

```sql
-- Отдельной колонки клиента нет, ключ хранится плоско — с `__` вместо `:`.
SELECT key, value FROM legacy_settings;
```

```sql
INSERT INTO legacy_settings (key, value) VALUES
    ('sa__pg__host',             'db-host'),                 -- → sa:pg:host
    ('sa__pg__port',             '5432'),                    -- → sa:pg:port
    ('sa__clients__acme__theme', 'light'),                   -- → sa:clients:acme:theme
    ('single_underscore_key',    'single_underscore_value'); -- одиночный `_` — данные
```

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.FromServices(),
    new PostgreSqlConfigurationOptions("SELECT key, value FROM legacy_settings")
    {
        DoubleUnderscoreAsSeparator = true,         // sa__pg__host ≡ sa:pg:host
    });
```

```csharp
using Sa.Configuration.Clients;

configuration["sa:pg:host"];                    // "db-host"
configuration["sa__pg__host"];                  // "db-host" — обе записи взаимозаменяемы
configuration.GetSection("sa:pg")["host"];      // "db-host" — путь стал настоящей иерархией
configuration.ForClient("acme")["theme"];       // "light" — клиентский слой собрался сам
```

Одиночное `_` остаётся частью имени (`single_underscore_key`). Замена выполняется до разрешения
дублей, поэтому `sa__pg__host` и `sa:pg:host` — одна настройка, и победителя между ними выбирает
`LastWins`. Если `__` в ключе — легитимная часть имени, а не разделитель, опцию включать нельзя:
при `false` ключ хранится и читается как есть.

Приём сочетается с вычисляемым клиентом: разрежьте ключ по первому `__`, а остаток оставьте как
есть — `DoubleUnderscoreAsSeparator` доведёт его до `:` (строка `acme__database__host` станет
`sa:clients:acme:database:host`).

Заранее отфильтрованный запрос тоже работает — константа `Client` говорит источнику, какой
префикс использовать:

```sql
SELECT key, value FROM settings WHERE client_id = @client_id;
```

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(
    connectionString,
    new PostgreSqlConfigurationOptions(
        "SELECT key, value FROM settings WHERE client_id = @client_id",
        new NpgsqlParameter("client_id", "acme"))
    {
        Client = "acme",                            // → sa:clients:acme:{key}
    });
```

Предпочтителен единый запрос: только он позволяет новому клиенту появиться после reload без
рестарта (фильтрованный запрос требует отдельного источника на клиента, зафиксированного при
сборке).

### Чтение клиента: вычисляемые ключи

Ключ клиента — обычный путь конфигурации, поэтому его можно вычислять, а не хардкодить. Это та же
форма, которую даёт `ClientColumn` в SQL:

```csharp
using Sa.Configuration.Clients;

// Единственное место, которое знает конвенцию — без разбросанных литералов "sa:clients:…".
static string ClientKey(string client, string setting)
    => $"{ClientConfiguration.DefaultSectionPath}:{client}:{setting}";

string? acmeTheme = configuration[ClientKey("acme", "theme")];   // "light"
string? betaTheme = configuration[ClientKey("beta", "theme")];   // null: у beta нет переопределения
```

Обычно удобнее оверлей, потому что он сам добавляет фолбэк на базовое значение:

```csharp
IConfiguration acme = configuration.ForClient("acme");

acme["theme"];          // "light"  — переопределение
acme["language"];       // "en"     — базовое значение
acme["database:host"];  // секции работают как обычно: дочерний ключ клиента выигрывает
```

А если и имена настроек динамические — вычисляются обе части:

```csharp
string[] settings = ["theme", "language", "notifications"];

foreach (string client in configuration.GetClientNames())
{
    IConfiguration view = configuration.ForClient(client);

    foreach (string setting in settings)
    {
        // view[setting] — оверлей; вычисленный ключ читает «сырое» значение с префиксом.
        string? value = view[setting];
        string raw = ClientKey(client, setting);

        Console.WriteLine($"{client}:{setting} = {value ?? "(нет)"} (raw {raw})");
    }
}
```

Правила:

- значение клиента выигрывает у базового по тому же пути; если у клиента значения нет — берётся
  базовое (оно никогда не выдумывается);
- клиенты не видят друг друга, а сама секция переопределений скрыта из клиентского представления;
- неизвестный клиент просто читает базовые значения — `ForClient` для него не бросает исключение.
  `IClientConfigurationFactory.TryFor` сообщает, объявлен ли клиент на самом деле;
- `GetClientNames()` / `IClientConfigurationFactory.Clients` пересчитываются при каждом чтении,
  поэтому клиент, добавленный в БД, появляется после `ReloadDynamicAsync()`.

### Dependency injection

```csharp
builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.KeyedClientConfigurations = true;     // keyed IConfiguration + IClientConfigurationFactory
});
```

Имя известно на этапе компиляции — keyed-инъекция:

```csharp
app.MapGet("/acme/theme", ([FromKeyedServices("acme")] IConfiguration acme) => acme["theme"]);
```

Имя известно только в рантайме — keyed-резолв или фабрика:

```csharp
app.MapGet("/settings/{client}", (string client, HttpContext http) =>
    http.RequestServices.GetRequiredKeyedService<IConfiguration>(client)["theme"]);

// Все клиенты, найденные по переопределениям:
app.MapGet("/settings", (IClientConfigurationFactory clients) =>
    clients.Clients.Select(id => new { Client = id, Theme = clients.For(id)["theme"] }));
```

`For`/`ForClient` возвращает тонкое «живое» представление: кэшируется только префикс, никогда —
значение, поэтому reload виден сразу, а один экземпляр безопасен для всех запросов.

### Алиасы внутри клиентского префикса

`UseClients()` учит pass алиасов работать по клиентам, поэтому клиент может держать настройки под
теми «плоскими» именами, которые приняты в его системе:

```csharp
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseClients();
    sa.UseSecrets(new SecretOptions
    {
        KeyAliases = new KeyAliasOptions
        {
            KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        },
    });
});
```

```text
sa:clients:acme:auth_base_url                      → sa:clients:acme:sa:scope:api:auth:base_url
```

Значения никогда не пересекают границу префикса: базовый алиас оставляет базовое значение, каждый
клиент разрешает своё, и «плоский» ключ одного клиента не утечёт ни в другого клиента, ни в базу.

---

## Обновление динамического слоя

`IConfigurationRoot.Reload()` перечитывает **все** провайдеры, включая файлы на диске. Когда нужно
обновить только слой БД, используйте runtime:

```csharp
ISaConfigurationReloader reloader = app.Services.GetRequiredService<ISaConfigurationReloader>();

await reloader.ReloadDynamicAsync(cancellationToken);
```

Что происходит: перезагружаются динамические провайдеры, затем pass (плейсхолдеры, алиасы, свои
трансформы) пересчитывается от свежих значений, затем поднимаются reload-токены. Статические
провайдеры не трогаются; их файловые watcher'ы работают как обычно.

Получение сервиса — и что работает в фоне:

```csharp
builder.Services.AddSaConfigurationRuntime(builder.Configuration);                 // только вручную, без службы
builder.Services.AddSaConfigurationRuntime(builder.Configuration, o =>
{
    o.InitialLoad = SaDynamicInitialLoad.Hosted;      // однократная загрузка до старта сервера
    o.DynamicReload = true;                           // периодический опрос — по явному включению
    o.DynamicReloadInterval = TimeSpan.FromSeconds(15);
});
```

| Опция | По умолчанию | Смысл |
|-------|--------------|-------|
| `InitialLoad` | `Explicit` | `Explicit` — ничего не происходит, пока вы не вызовете `EnsureInitializedAsync`; `Hosted` — однократный хук грузит до первого запроса, ошибка валит старт; `Background` — хост стартует сразу, ошибка логируется |
| `DynamicReload` | `false` | регистрирует ровно одну hosted-службу с `PeriodicTimer`; без неё таймера нет |
| `DynamicReloadInterval` | 30 с | период опроса |
| `DynamicReloadInitialDelay` | `null` | задержка перед первым опросом (по умолчанию — один интервал) |

Позднее связывание (`PgConfigurationConnection.FromServices()`) резолвится из контейнера во время
`EnsureInitializedAsync`, то есть после `builder.Build()`; при явных `DataSource`/`ConnectionString`
шаг инициализации не нужен. Инициализация идемпотентна, а неудачный периодический reload
логируется, и предыдущий снапшот остаётся в силе.

---

## Полный пример DI

Статика + динамика + два клиента, один пул соединений и никакой фоновой службы без явной просьбы:

```csharp
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Статика: хост уже добавил appsettings/env/args, поэтому UseStandardSources здесь ничего не делает.
// Динамический источник читает через пул, зарегистрированный ниже.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets();                                   // {{placeholder}} + алиасы ключей
    sa.UseDynamic(new DatabaseConfigurationSource(
        new PostgreSqlConfigurationOptions("SELECT client_id, key, value FROM settings")
        {
            ClientColumn = "client_id",
        },
        PgConfigurationConnection.FromServices("orders")));
    sa.UseClients();
});

builder.Services.AddSaPostgreSqlDataSource("orders", b => b.FromConfiguration("Postgres"));
builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.KeyedClientConfigurations = true;
    runtime.InitialLoad = SaDynamicInitialLoad.Hosted;   // до первого запроса
});

var app = builder.Build();

app.MapGet("/settings", (IConfiguration configuration) => new
{
    Theme = configuration["theme"],                     // базовое значение
    Clients = configuration.GetClientNames(),           // ["acme", "beta"]
});

app.MapGet("/settings/{client}", (string client, IConfiguration configuration) =>
{
    IConfiguration view = configuration.ForClient(client);
    return new { Client = client, Theme = view["theme"], Language = view["language"] };
});

app.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
    await reloader.ReloadDynamicAsync());

app.Run();
```

Пример данных `settings`:

```sql
CREATE TABLE settings (client_id TEXT, key TEXT NOT NULL, value TEXT NOT NULL);

INSERT INTO settings (client_id, key, value) VALUES
    (NULL,   'theme',    'dark'),   -- базовое значение для всех
    (NULL,   'language', 'en'),
    ('acme', 'theme',    'light'),  -- acme перекрывает только тему
    ('beta', 'language', 'de');     -- beta перекрывает только язык
```

`GET /settings` → базовые `dark`/`en`; `GET /settings/acme` → `light`/`en`;
`GET /settings/beta` → `dark`/`de`; `GET /settings/unknown` → `dark`/`en`.

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
