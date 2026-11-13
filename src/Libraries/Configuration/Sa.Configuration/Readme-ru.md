# Sa.Configuration

**Русский** | [English](Readme.md)

Пакет для .NET, который собирает и расширяет конфигурацию:

1. **Собирает стек слоёв** — статические источники (appsettings, переменные окружения, аргументы, секреты) и динамические (БД) в одном детерминированном порядке, с builder'ом под две стратегии.
2. **Подставляет секреты в конфигурацию** — вместо того чтобы хардкодить пароли и ключи, вы пишете в конфиге `{{sa_pg_password}}`, а пакет сам подставляет настоящее значение из файла, переменной окружения или аргумента командной строки.
3. **Накладывает клиентские настройки** — у каждого клиента свой корень конфигурации с теми же именами ключей, положенный поверх общих значений, и доступ к нему — как к keyed `IConfiguration`.
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
- [Клиенты (ядро + оверлеи клиентов)](#клиенты-ядро--оверлеи-клиентов)
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
- **Конфигурация клиентов** — ядро хранит общие ключи, каждый клиент получает свой корень с теми же именами ключей, собранный из плоских строк провайдеров, и доступен как keyed `IConfiguration`. См. [Клиенты](#клиенты-ядро--оверлеи-клиентов).
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
// Стратегия 2 — статика + динамика ядра. Клиенты регистрируются отдельно (см. «Клиенты»).
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseStandardSources();                                                // статика
    sa.UseSecrets();                                                        // {{…}} + алиасы
    sa.UseDynamic(new DatabaseConfigurationSource(                          // динамика, перекрывает статику
        new PostgreSqlConfigurationOptions("SELECT key, value FROM setting"),
        PgConfigurationConnection.FromServices()));
});
```

| Метод | Что добавляет |
|-------|---------------|
| `UseStandardSources(...)` | `appsettings.json`, `appsettings.{env}.json`, переменные окружения, аргументы. **Идемпотентен**: в ASP.NET Core / generic host хост уже добавил их, и метод ничего не делает. |
| `UseSecrets(...)` | прежний конвейер секретов: плейсхолдеры, алиасы, источник аргументов. |
| `UseDynamic(source)` | источник, значения которого меняются в рантайме; ставится после статических. |
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
зарегистрирован позже). Формы запросов и детали обновления — в README пакета.

---

## Клиенты (ядро + оверлеи клиентов)

Ядро конфигурации хранит общие, фиксированные ключи. Клиент ничего не префиксует: у него **свой
корень конфигурации** с **теми же именами ключей**, положенный поверх общих значений. Чтение
клиента сначала берёт его собственное значение, а если его нет — падает на значение ядра.

```text
ядро ("s3_url" = "A")                     ← общее значение
client1 ("s3_url" = "B1")                 ← отдельный корень, то же имя ключа
client2 ("s3_url" = "C")                  ← отдельный корень, то же имя ключа

cf["s3_url"]                             → "A"
clients.GetClient("client1")["s3_url"]   → "B1"
clients.GetClient("client2")["s3_url"]   → "C"
```

Префикса `sa:clients:{client}:{key}` и общего словаря нет: корни клиентов независимы, поэтому
значения одного клиента не могут протечь ни в ядро, ни в другого клиента. Имена ключей — это
конвенция, а не схема: клиент может завести ключи, которых в ядре нет.

### Данные: таблица ядра + `client_settings`

Каноническая форма — две таблицы. Общие значения лежат в `setting`, переопределения клиентов — в
`client_settings` с разбиением по `client_id`:

```sql
CREATE TABLE setting (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

INSERT INTO setting (key, value) VALUES
    ('s3_url',   'A'),
    ('theme',    'dark'),
    ('language', 'en');

CREATE TABLE client_settings (
    client_id TEXT NOT NULL,
    key       TEXT NOT NULL,
    value     TEXT NOT NULL,
    PRIMARY KEY (client_id, key)
);

INSERT INTO client_settings (client_id, key, value) VALUES
    ('client1', 's3_url', 'B1'),
    ('client1', 'theme',  'light'),
    ('client2', 's3_url', 'C'),
    ('client2', 'language', 'de');
```

### Регистрация: провайдеры, возвращающие плоские строки

Провайдер — это колбэк, зарегистрированный через `clients.Add<T1[,T2[,T3]]>(...)`. Его параметры
(от одного до трёх, плюс `CancellationToken`) достаются из контейнера в момент чтения данных, а
возвращает он плоские строки `(client_id, key, value)` сразу по **всем** клиентам. Реестр
группирует строки по `client_id` и строит по корню на клиента, так что провайдер ничего не знает
про корни клиентов, сплиттеры и префиксы — он просто отдаёт строки.

```csharp
using Sa.Configuration.Clients;

builder.Services.AddSingleton<IClientSettingsStore, PgClientSettingsStore>();

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    // До трёх зависимостей приходят из DI; колбэк возвращает строки по всем клиентам.
    clients.Add<IClientSettingsStore>(
        (store, cancellationToken) => store.LoadAllAsync(cancellationToken));
});
```

Тип строки — `ClientSetting(clientId, key, value)`, ровно та форма, которую возвращает запрос:

```csharp
public async ValueTask<IReadOnlyList<ClientSetting>> LoadAllAsync(CancellationToken ct)
{
    List<ClientSetting> rows = [];

    await dataSource.ExecuteReaderAsync(
        "SELECT client_id, key, value FROM client_settings",
        (reader, _) => rows.Add(new ClientSetting(
            reader.GetString(0), reader.GetString(1), reader.GetString(2))),
        initCommand: null,
        ct);

    return rows;
}
```

Клиента не нужно объявлять отдельно: он существует, как только хоть один провайдер вернул для него
строку. Провайдеры вызываются лениво при первом обращении и снова при `ReloadAsync()` /
`ReloadDynamicAsync()`, поэтому новая строка в `client_settings` становится доступной без рестарта.

Провайдеров можно зарегистрировать несколько — их строки объединяются по клиенту; при совпадении
ключа побеждает строка, пришедшая позже:

```csharp
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IClientSettingsStore>((store, ct) => store.LoadAllAsync(ct));

    clients.Add<IDefaultsStore, ILogger<Clients>>(
        (defaults, logger, ct) => defaults.LoadAsync(logger, ct));
});
```

### Клиенты из секции конфигурации (без базы)

Если клиенты уже лежат в статической конфигурации, `UseConfiguration` регистрирует такой же
провайдер поверх секции. Дочерние элементы секции — это id клиентов; ключи внутри секции клиента
называются так же, как в ядре, и читаются **без** префикса секции.

```json
{
  "s3_url": "A",
  "theme": "dark",
  "language": "en",

  "clients": {
    "acme": { "s3_url": "B1", "theme": "light" },
    "beta": { "s3_url": "C", "language": "de" }
  }
}
```

```csharp
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
    clients.UseConfiguration(builder.Configuration));
```

`clients.Clients` → `["acme", "beta"]`; `clients.GetClient("acme")["s3_url"]` → `"B1"`;
`clients.GetClient("acme")["language"]` → `"en"` (значение ядра). Реестр «живой»: клиент,
добавленный в секцию, появляется после reload, а значения существующих клиентов перечитываются при
каждом `ReloadDynamicAsync()`.

Другая раскладка — меняется только путь секции:

```csharp
clients.UseConfiguration(builder.Configuration, new ConfigurationClientSourceOptions
{
    SectionPath = "tenants",        // tenants:acme:s3_url, tenants:beta:s3_url, …
});
```

`UseConfiguration` — такой же провайдер, как остальные, поэтому его можно сочетать с
`clients.Add<T>(...)`; строки всех провайдеров объединяются по клиенту.

### Варианты регистрации в DI

| Форма | Когда использовать |
|---|---|
| `clients.Add<T1>((t1, ct) => rows)` | провайдеру нужен один сервис DI, возвращает строки по всем клиентам |
| `clients.Add<T1, T2>((t1, t2, ct) => rows)` | два сервиса (например, хранилище и логгер) |
| `clients.Add<T1, T2, T3>((t1, t2, t3, ct) => rows)` | три сервиса |
| `clients.UseConfiguration(configuration)` | клиенты и их значения лежат в секции конфигурации (`clients:{id}:{key}`), база не нужна |
| `AttachAsync(client, source)` | клиент появляется в рантайме (плагин, тенант, поднятый вручную, тест) |
| `clients.Configure(o => …)` | настроить `ThrowForUnknownClient` / `EagerLoad` |

### Чтение

```csharp
public sealed class SettingsEndpoints(IConfiguration core, IClientConfigurations clients)
{
    public string CoreS3Url => core["s3_url"]!;                              // "A"
    public string Client1S3Url => clients.GetClient("client1")["s3_url"]!;   // "B1"
    public string Client2S3Url => clients.GetClient("client2")["s3_url"]!;   // "C"

    public IReadOnlyCollection<string> Known => clients.Clients;              // из провайдеров + добавленные
}
```

Правила:

- значение клиента выигрывает у значения ядра по тому же ключу; если у клиента значения нет —
  берётся значение ядра, и оно никогда не выдумывается;
- клиент видит только свой корень плюс ядро — никогда значения другого клиента; у секционного
  провайдера секция клиентов — это метаданные оверлея, и она скрыта из каждого представления,
  поэтому один клиент не прочитает строки другого через фолбэк на ядро;
- пустая строка (`value` пустой или из пробелов) отбрасывается при чтении строк и делегирует значение
  ядра — `ClientConfigurationOptions.SkipEmptyValues` (по умолчанию включён) повторяет правило слоя
  ядра; `false` делает пустую строку явным собственным значением. Пустое значение, полученное
  преобразованием, записывается после чтения строк и всегда остаётся локальным у клиента;
- неизвестный клиент читает значения ядра; `TryGet` сообщает, известен ли он на самом деле, и не
  бросает исключений даже при `ThrowForUnknownClient` (бросает только `GetClient`);
- `Clients` — объединение id, которые вернули провайдеры, и клиентов, добавленных в рантайме; поэтому
  клиент, добавленный в БД, появляется после `ReloadDynamicAsync()`;
- **строки переписываются тем же проходом, что и ядро** — значения клиента тоже проходят цепочку
  преобразований: секреты `{{placeholder}}` подставляются, а алиасы ключей раскрываются внутри
  собственных строк клиента (источник алиаса ищется сначала у клиента, потом в ядре). Производное
  значение, которое лишь повторяет ядро, в оверлей не копируется — клиент продолжает читать ядро
  «живьём». Нераскрытое значение (отсутствующий секрет) читается у клиента как пустая строка, а
  не как значение ядра и не как литеральный `{{…}}`. Исключение — секционный провайдер
  (`UseConfiguration`): проход ядра обнуляет нераскрытый плейсхолдер **до** чтения секции, поэтому
  такая строка до реестра не доходит и клиент берёт значение ядра;
- ключи используют каноническую запись с `:`: `sa__pg__host` в строке — та же настройка, что
  `sa:pg:host`.

Секции работают поверх обоих корней:

```csharp
IConfiguration db = clients.GetClient("client1").GetSection("db");
db["host"]; // значение client1, если оно есть, иначе значение ядра
```

### Алиасы и плейсхолдеры внутри оверлея клиента

Строки клиента проходят тот же проход, что и ядро, поэтому клиент может держать свои настройки под
теми плоскими именами, которые использует его собственная система:

```csharp
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets(new SecretOptions
    {
        KeyAliases = new KeyAliasOptions
        {
            KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        },
    });
});

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
    clients.Add<IClientSettingsStore>((store, ct) => store.LoadAllAsync(ct)));
```

```text
ядро:    auth_base_url = https://core.example.com   → sa:scope:api:auth:base_url = https://core.example.com
client1: auth_base_url = https://acme.example.com   → sa:scope:api:auth:base_url = https://acme.example.com
client2: (такой строки нет)                         → https://core.example.com, читается «живьём» из ядра

client1: api_token = {{acme_token}}                 → подставленный секрет, никогда литерал
```

Источник алиаса ищется сначала у клиента, потом в ядре; значение клиента не пересекает границу ни в
ядро, ни в другого клиента. Производное значение, которое лишь повторяет ядро, в оверлей не
копируется, поэтому изменение в ядре видно без перезагрузки строк клиента.

### Keyed-инъекция

Когда id клиента известен на этапе компиляции, его конфигурация достаётся по ключу — обработчик
или конструктор получает собственный корень клиента напрямую.

```csharp
using Microsoft.Extensions.DependencyInjection;   // [FromKeyedServices]

public static class Clients
{
    public const string Acme = "acme";
    public const string Beta = "beta";
}

builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
    clients.UseConfiguration(builder.Configuration));

// Minimal API: известный клиент приходит из контейнера, в теле обработчика нет обращения к реестру.
app.MapGet("/acme/settings", ([FromKeyedServices(Clients.Acme)] IConfiguration acme) => new
{
    S3Url = acme["s3_url"],        // "B1"
    Theme = acme["theme"],         // "light"
    Language = acme["language"],   // "en" — фолбэк на ядро
});

// Обычный сервис: та же keyed-инъекция в конструкторе.
public sealed class AcmeStorage([FromKeyedServices(Clients.Acme)] IConfiguration configuration)
{
    public string S3Url => configuration["s3_url"]!;
}

// Код, у которого есть IServiceProvider (фабрика, стартовая задача):
IConfiguration beta = app.Services.GetRequiredKeyedService<IConfiguration>(Clients.Beta);
beta["s3_url"];                    // "C"
```

Ключ — это id клиента из `client_settings.client_id` (или дочерний ключ секции), сравнение
регистронезависимое. Инъекция выполняется при каждом разрешении — клиент, добавленный или
обновлённый позже, виден на следующем запросе без перерегистрации.

Имя, известное только в рантайме, ключом на этапе компиляции быть не может, поэтому передаём id и
спрашиваем реестр:

```csharp
app.MapGet("/settings/{client}", (string client, IClientConfigurations clients) =>
    new { Client = client, S3Url = clients.GetClient(client)["s3_url"] });
```

Keyed-регистрация использует `KeyedService.AnyKey`, поэтому `GetRequiredKeyedService<IConfiguration>(id)`
работает для имени, которое существует только в рантайме. Перечисление keyed-сервисов не
перечисляет клиентов — для этого есть `IClientConfigurations.Clients`.

### Подключение и удаление в рантайме

```csharp
await clients.AttachAsync("acme", new MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?> { ["s3_url"] = "B1" },
});

await clients.RemoveAsync("acme");   // представление снова падает на значения ядра
```

`AttachAsync` заменяет существующий оверлей того же клиента и освобождает заменённый. `RemoveAsync`
возвращает `false` и ничего не меняет для клиента, которого по-прежнему отдаёт провайдер: его оверлей
всё равно был бы собран заново при следующем обращении, поэтому вызов честно сообщает, что удалять
было нечего. Удалить можно только вложение, добавленное в рантайме.

Представления, возвращённые `GetClient`, «живые»: при каждом обращении они спрашивают у реестра
текущий корень, поэтому attach/remove/reload виден через ссылку, полученную раньше — в том числе для
клиента, неизвестного в момент получения ссылки. То же верно для change-токена: `GetReloadToken()`
представления композитит ядро, оверлей и реестр, поэтому колбэк срабатывает и на attach/remove, а не
только на перезагрузке. Реестр потокобезопасен.

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

Статика + динамика ядра + два клиента, один пул соединений и никакой фоновой службы без явной
просьбы:

```csharp
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Статика: хост уже добавил appsettings/env/args, поэтому UseStandardSources здесь ничего не делает.
// Динамический источник читает общие значения через пул, зарегистрированный ниже.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets();                                   // {{placeholder}} + алиасы ключей
    sa.UseDynamic(new DatabaseConfigurationSource(
        new PostgreSqlConfigurationOptions("SELECT key, value FROM setting"),
        PgConfigurationConnection.FromServices("orders")));
});

builder.Services.AddSaPostgreSqlDataSource("orders", b => b.FromConfiguration("Postgres"));
builder.Services.AddSingleton<IClientSettingsStore, PgClientSettingsStore>();

// Каждый клиент получает свой корень; ключи хранятся как есть, без префикса. Провайдер достаёт
// хранилище из DI и возвращает строки `(client_id, key, value)` сразу по всем клиентам.
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IClientSettingsStore>((store, ct) => store.LoadAllAsync(ct));
});

builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.InitialLoad = SaDynamicInitialLoad.Hosted;   // до первого запроса
});

var app = builder.Build();

app.MapGet("/settings", (IConfiguration configuration, IClientConfigurations clients) => new
{
    Theme = configuration["theme"],                     // значение ядра
    S3Url = configuration["s3_url"],                    // значение ядра
    Clients = clients.Clients,                          // ["acme", "beta"]
});

app.MapGet("/settings/{client}", (string client, IClientConfigurations clients) =>
{
    IConfiguration view = clients.GetClient(client);
    return new
    {
        Client = client,
        Theme = view["theme"],
        S3Url = view["s3_url"],
        Language = view["language"],
    };
});

app.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
    await reloader.ReloadDynamicAsync());

app.Run();
```

Провайдер за `clients.Add<IClientSettingsStore>(...)` — один запрос, плоские строки, группировку
делает реестр:

```csharp
public interface IClientSettingsStore
{
    ValueTask<IReadOnlyList<ClientSetting>> LoadAllAsync(CancellationToken ct);
}

public sealed class PgClientSettingsStore(IPgDataSource dataSource) : IClientSettingsStore
{
    public async ValueTask<IReadOnlyList<ClientSetting>> LoadAllAsync(CancellationToken ct)
    {
        List<ClientSetting> rows = [];

        await dataSource.ExecuteReaderAsync(
            "SELECT client_id, key, value FROM client_settings",
            (reader, _) => rows.Add(new ClientSetting(
                reader.GetString(0), reader.GetString(1), reader.GetString(2))),
            initCommand: null,
            ct);

        return rows;
    }
}
```

Пример данных — таблица ядра и таблица клиентов:

```sql
CREATE TABLE setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);

INSERT INTO setting (key, value) VALUES
    ('s3_url',   'A'),      -- значение ядра для всех клиентов
    ('theme',    'dark'),
    ('language', 'en');

CREATE TABLE client_settings (
    client_id TEXT NOT NULL,
    key       TEXT NOT NULL,
    value     TEXT NOT NULL,
    PRIMARY KEY (client_id, key)
);

INSERT INTO client_settings (client_id, key, value) VALUES
    ('acme', 's3_url',   'B1'),   -- acme перекрывает только s3_url
    ('acme', 'theme',    'light'),
    ('beta', 's3_url',   'C'),    -- у beta своё значение
    ('beta', 'language', 'de');
```

`GET /settings` → `s3_url` = `A`, `theme` = `dark`, `language` = `en`;
`GET /settings/acme` → `B1` / `light` / `en`;
`GET /settings/beta` → `C` / `dark` / `de`;
`GET /settings/unknown` → `A` / `dark` / `en`.

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
