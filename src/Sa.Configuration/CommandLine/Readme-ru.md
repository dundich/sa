# Arguments — Парсинг аргументов командной строки

Парсинг и потребление аргументов командной строки в .NET-приложениях через простой dictionary-like API. Поддерживает форматы `--flag=value`, `--flag value`, короткие опции (`-x`) и типизированные геттеры.

> **Важно:** парсер удаляет ведущие тире из имён параметров. При обращении к значению используйте ключ **без** лидирующих `-` или `--`.  
> Пример: `--config_db` в CLI → `args["config_db"]` в коде. `-v` в CLI → `args["v"]` в коде.

## Быстрый старт

```csharp
using Sa.Configuration.CommandLine;

// Парсим args (по умолчанию берёт Environment.GetCommandLineArgs())
var args = new Arguments(args);

// Доступ через индексатор (возвращает null, если ключ отсутствует) — ключи без ведущих тире
string? db    = args["config_db"];
string? file  = args["config_file"];
bool    debug = args.IsPresent("debug");   // true если флаг присутствует и истинен

// Типизированные помощники (возвращают nullable, null при отсутствии/невалидности)
int?     port    = args.GetInt("port");
float?   timeout = args.GetFloat("timeout");
long     offset  = args.GetLong("offset");
TimeSpan ttl     = args.GetTimeSpan("ttl");
bool     verbose = args.GetBool("v");       // "true"/"1"/"yes"/"on" → true
```

### Минимальное консольное приложение

```csharp
using Sa.Configuration.CommandLine;

var arguments = new Arguments(args);

Console.WriteLine($"БД:    {arguments["db"]    ?? "(по умолчанию)"}");
Console.WriteLine($"Порт:  {arguments.GetInt("port")    ?? 5432}");
Console.WriteLine($"Debug: {arguments.IsPresent("debug")}");
Console.WriteLine($"TTL:   {arguments.GetTimeSpan("ttl") ?? TimeSpan.Zero}");
```

Запуск:

```bash
dotnet run -- --db mydb --port 9999 --debug --ttl 00:05:00 -v
```

Вывод:

```
БД:    mydb
Порт:  9999
Debug: True
TTL:   00:05:00
```

---

## Поддерживаемые форматы

| Формат | Ввод в CLI | Ключ в словаре | Значение |
|--------|-----------|---------------|---------|
| Длинный флаг + пробел | `--key value` | `"key"` | `"value"` |
| Равно | `--key=value` | `"key"` | `"value"` |
| Короткий флаг + пробел | `-k value` | `"k"` | `"value"` |
| Короткое равно | `-k=v` | `"k"` | `"v"` |
| Булев флаг | `--debug` | `"debug"` | `"true"` |
| Значение в кавычках | `--name "hello world"` | `"name"` | `"hello world"` |

---

## Справочник API

### Конструктор

```csharp
public Arguments(params IReadOnlyList<string> args)
```

Создаёт экземпляр из списка строк аргументов.

### Статическая фабрика

```csharp
public static Arguments CreateDefault(string[]? args = null)
```

Шорткат, который использует `Environment.GetCommandLineArgs()` когда `args` равен null.

```csharp
var args = Arguments.CreateDefault();   // читает Process.GetCurrentProcess().CommandLine
```

### Индексатор

```csharp
public string? this[string param] { get; }
```

Возвращает значение по имени параметра или `null`, если не найдено. Ключи хранятся без ведущих тире.

```csharp
var db = args["database"];   // null если --database никогда не передавали
```

### Contains / IsPresent

```csharp
public bool Contains(string param)           // true если ключ существует (даже если значение пустое)
public bool IsPresent(string param)          // true если ключ существует И значение не null
```

`IsPresent` различает отсутствующий флаг и присутствующий, но пустой.

### Типизированные геттеры

Все возвращают `T?` (nullable) и дают `null` когда параметр отсутствует или не распарсивается.

| Метод | Тип возврата | Пример |
|-------|-------------|--------|
| `GetBool(string)` | `bool?` | `args.GetBool("verbose")` — принимает `true/1/yes/on` |
| `GetInt(string)`  | `int?`  | `args.GetInt("port")` |
| `GetFloat(string)`| `float?`| `args.GetFloat("ratio")` |
| `GetLong(string)` | `long?` | `args.GetLong("offset")` |
| `GetTimeSpan(string)` | `TimeSpan?` | `args.GetTimeSpan("delay")` |

Все числовые парсинги используют `CultureInfo.InvariantCulture`.

### Исходные параметры

```csharp
public IReadOnlyDictionary<string, string?> Parameters { get; }
```

Возвращает полный словарь распарсенных параметров. Ключи хранятся без ведущих тире.

---

## Интеграция с Microsoft.Extensions.Configuration

Регистрация аргументов командной строки как источника `IConfiguration`:

```csharp
using Microsoft.Extensions.Configuration;
using Sa.Configuration.CommandLine;

var configuration = new ConfigurationBuilder()
    .AddSaCommandLine(args)        // <-- добавляет CLI аргументы как источник конфига
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

// Доступ через индексатор IConfiguration — ключи тоже без тире
var db = configuration["db"];
var port = configuration["port"];
```

Порядок важен: источники, зарегистрированные **позже**, переопределяют ранние. Размещайте `AddSaCommandLine` перед JSON/файловыми источниками, если хотите, чтобы CLI имел приоритет:

```csharp
new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")      // базовые значения по умолчанию
    .AddSaCommandLine(args)                // переопределения из CLI
    .Build();
```

---

## Запуск приложения

### Из терминала

```bash
# Длинные флаги с разделителем пробелом
dotnet run --project MyApp.dll --db production --port 5432 --debug

# Синтаксис с равно
dotnet run --project MyApp.dll --db=production --port=5432

# Смешанные форматы
dotnet run -- -d --db=prod -p 3306 --ttl 30s
```

### Из Visual Studio / VS Code

Установите аргументы в launchSettings.json:

```json
{
  "profiles": {
    "MyApp": {
      "commandName": "Project",
      "commandLineArgs": "--db test --port 9999 --debug --ttl 00:01:00"
    }
  }
}
```

---

## Краевые случаи

| Ввод в CLI | Ключ в словаре | Значение |
|-----------|---------------|---------|
| `--flag` (без значения) | `"flag"` | `"true"` |
| `--flag=` (пустое) | `"flag"` | `""` |
| `--flag "quoted value"` | `"flag"` | `"quoted value"` |
| `-short=value` | `"short"` | `"value"` |
| Неизвестный формат | Игнорируется молча | — |
