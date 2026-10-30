# Sa — Общие утилиты

Базовая библиотека утилит экосистемы **Sa**. Содержит общие классы и методы расширения, которые другие пакеты потребляют через `<Compile Include="..." Link="..."/>`. Целевая платформа — **.NET 10.0**, совместима с **Native AOT**.

---

## Классы (пространство имён `Sa.Classes`)

### LockRenewer

Автоматическое продление блокировки с настраиваемым интервалом продления на основе `PeriodicTimer`.

| Метод | Описание |
|--------|-------------|
| `KeepLocked(TimeSpan, Func<CancellationToken, Task>, bool, CancellationToken)` | Запускает фоновую задачу, периодически продлевающую блокировку. Возвращает `IAsyncDisposable` для корректного завершения. |
| `WaitForConditionAsync(Func<CancellationToken, Task<bool>>, TimeSpan, TimeSpan?, CancellationToken)` | Опрашивает предикат, пока он не вернёт `true`, не истечёт таймаут или не поступит запрос на отмену. |

```csharp
var disposable = LockRenewer.KeepLocked(
    lockExpiration: TimeSpan.FromSeconds(30),
    extendLocked: ct => db.ExtendLockAsync(resourceId, ct),
    blockImmediately: true);

// ... позже
await disposable.DisposeAsync();
```

### MurmurHash3

Компактная быстрая хеш-функция для идентификации типов и партиционирования.

| Метод | Описание |
|--------|-------------|
| `Hash32(ReadOnlySpan<byte>, uint)` | Вычисляет 32-битный хеш MurmurHash3 с заданным seed. Удобно со stackalloc. |

```csharp
var bytes = Encoding.UTF8.GetBytes("partition-key");
uint hash = MurmurHash3.Hash32(bytes, seed: 0);
int partitionIndex = (int)(hash % numberOfPartitions);
```

### Retry

Хелперы повторных попыток с четырьмя стратегиями: **Constant**, **Linear**, **Exponential** и **Jitter** (декоррелированный джиттер в стиле Azure).

#### Стратегии

| Стратегия | Параметры | Поведение |
|----------|------------|----------|
| `Constant` | `delay`, `fastFirst` | Фиксированная задержка между попытками. `fastFirst: true` пропускает начальную задержку. |
| `Linear` | `firstDelay`, `increment`, `maxDelay`, `count` | Задержка растёт линейно: `firstDelay + n * increment`, ограничена `maxDelay`. |
| `Exponential` | `firstDelay`, `factor`, `maxDelay`, `count` | Задержка удваивается с каждой попыткой: `firstDelay * factor^n`, ограничена `maxDelay`. |
| `Jitter` | `strategy`, `jitterSpan` | Добавляет случайный +/- джиттер к задержке любой стратегии. |

#### Выполнение

| Метод | Описание |
|--------|-------------|
| `WaitAndRetry(IEnumerable<TimeSpan>, Func<Task>, CancellationToken)` | Выполняет функцию с задержками между попытками. Бросает последнее исключение, если отмена поступила во время задержки после сбоя. |

```csharp
// Экспоненциальная выдержка: 100ms → 200ms → 400ms → 800ms → 1600ms (максимум)
var strategy = Retry.Exponential(firstDelay: TimeSpan.FromMilliseconds(100), count: 5);
await Retry.WaitAndRetry(strategy, () => CallExternalApiAsync(), cancellationToken);
```

### ResetLazy\<T\>

Лениво вычисляемое перезагружаемое кэшированное значение с тремя режимами потокобезопасности.

| Свойство/Метод | Описание |
|-----------------|-------------|
| `Value` | Лениво инициализирует и возвращает кэшированное значение. |
| `IsValueCreated` | `true`, если фабрика уже была вызвана. |
| `Load()` | Принудительная инициализация (не делает ничего, если значение уже создано). |
| `Reset()` | Очищает кэш, при необходимости вызывая колбэк `valueReset` со старым значением. |

```csharp
var lazy = new ResetLazy<MyConfig>(() => ConfigLoader.Load(), valueReset: cfg => cfg.Dispose());
var config = lazy.Value;        // первое обращение запускает фабрику
lazy.Reset();                   // очищает кэш, вызывает Dispose у старого значения
config = lazy.Value;            // фабрика вызывается снова
```

### Levenshtein

Алгоритм расстояния Дамерау — Левенштейна для сравнения строк на схожесть. Оптимизирован через stackalloc для минимальных аллокаций.

| Метод | Описание |
|--------|-------------|
| `Distance(string?, string?)` | Возвращает расстояние правок (0 — точное совпадение). Безопасно для `null`. |
| `GetSimilarity(string?, string?)` | Возвращает коэффициент схожести 0.0–1.0 относительно более длинной строки. |
| `IsSimilar(string?, string?, double)` | Проверяет, что схожесть ≥ порога (по умолчанию 0.8). |

#### Levenshtein.Matcher

Нечёткое сопоставление по коллекциям (generic).

| Метод | Описание |
|--------|-------------|
| `FindMatches<T>(string?, IEnumerable<T>, Func<T, string?>, double, bool)` | Перечисляет все совпадения выше `similarityThreshold`. По умолчанию нормализует строки. |
| `FindBestMatch<T>(...)` | Возвращает лучшее одиночное совпадение (максимальная схожесть). |
| `FindBestMatch(IEnumerable<MatchResult<T>>)` | Выбирает лучшее из предварительно отфильтрованных совпадений. |
| `FindBestMatch(string?, params string?[])` | Перегрузка для массива обычных строк. Возвращает `(bestMatch, distance)`. |

```csharp
var best = Levenshtein.Matcher.FindBestMatch(
    source: "recieve",
    candidates: ["receive", "relief", "refuse"]);
// best.bestMatch == "receive", best.distance == 1

bool similar = Levenshtein.IsSimilar("hello", "hallo", threshold: 0.8);
// true
```

### MimeTypeMap

Полноценный поиск MIME-типа по расширению файла или имени файла (1000+ маппингов из Windows Registry + IANA).

| Метод | Описание |
|--------|-------------|
| `TryGetMimeType(string, out string?)` | Ищет MIME-тип по имени файла или расширению. Автоматически отбрасывает query-строки. |
| `GetMimeType(string)` | Как `TryGetMimeType`, но при отсутствии возвращает `"application/octet-stream"`. |
| `GetExtension(string, bool)` | Обратный поиск: MIME-тип → расширение. |

```csharp
string? mime;
if (MimeTypeMap.TryGetMimeType("document.pdf", out mime))
{
    Console.WriteLine(mime); // "application/pdf"
}

string ext = MimeTypeMap.GetExtension("image/png"); // ".png"
```

### ProcessExecutor / IProcessExecutor

Асинхронный исполнитель процессов с выводом в реальном времени, передачей stdin и надёжным управлением жизненным циклом.

| Метод | Описание |
|--------|-------------|
| `ExecuteAsync(ProcessStartInfo, Action<string>?, Action<string>?, TimeSpan?, CancellationToken)` | Колбэки stdout/stderr в реальном времени. |
| `ExecuteWithResultAsync(...)` | Собирает полный вывод в `ProcessExecutionResult`. |
| `ExecuteStdOutAsync(...)` | Потоково передаёт stdout в колбэк, прокачивая stdin и собирая stderr. |

#### Публичные типы

| Тип | Описание |
|------|-------------|
| `ProcessExecutionResult` | Запись: `(int ExitCode, string StandardOutput, string StandardError)` |
| `ProcessExecutionException` | Бросается при ненулевом коде выхода, свойство `Exitcode`. |
| `ProcessExecutionResultException` | Оборачивает `ProcessExecutionResult` как исключение. |
| `ProcessStartException` | Бросается, если `Process.Start()` завершился неудачей. |
| `ProcessTimeoutException` | Бросается при таймауте выполнения. |

```csharp
var result = await IProcessExecutor.Default.ExecuteWithResultAsync(new ProcessStartInfo
{
    FileName = "ffmpeg",
    Arguments = "-i input.mp4 -vn -ab 128k output.mp3",
    RedirectStandardOutput = true,
    RedirectStandardError = true
});

if (result.ExitCode != 0)
    Console.WriteLine(result.StandardError);
```

---

## Расширения (пространство имён `Sa.Extensions`)

### DateTimeExtensions

| Метод | Описание |
|--------|-------------|
| `ToUnixTimestamp(bool isInMilliseconds)` | Преобразует `DateTime` в Unix epoch в секундах или миллисекундах. Не-UTC автоматически конвертируется в UTC. |
| `StartOfDay()` | Возвращает `DateTimeOffset` с полночью и тем же смещением. |
| `EndOfDay()` | Возвращает `DateTimeOffset` с началом следующего дня (верхняя граница не включается). |
| `StartOfMonth()` / `EndOfMonth()` | Границы первого/следующего дня месяца. |
| `StartOfYear()` / `EndOfYear()` | Границы первого/следующего года. |

```csharp
var ts = DateTime.UtcNow.ToUnixTimestamp();           // секунды
var ms = someDate.ToUnixTimestamp(isInMilliseconds);  // миллисекунды
var today = dto.StartOfDay();
```

### NumericExtensions

| Метод | Описание |
|--------|-------------|
| `ToDateTimeFromUnixTimestamp(this uint)` | Unix timestamp → UTC `DateTime` (автоопределение секунд или миллисекунд). |
| `ToDateTimeFromUnixTimestamp(this long)` | То же для знакового 64-бит. |
| `ToDateTimeFromUnixTimestamp(this ulong)` | Для беззнакового 64-бит. |
| `ToDateTimeFromUnixTimestamp(this double)` | Вещественные секунды, усечение до long. |
| `ToDateTimeFromUnixTimestamp(this string)` | Парсит строку → long → DateTime; возвращает `null` при неудаче. |
| Nullable-перегрузки (`long?`, `ulong?`, `double?`) | Возвращают `null` при `null` на входе. |
| `ToDateTimeOffsetFromUnixTimestamp(this long)` | Timestamp → `DateTimeOffset`. |

```csharp
DateTime dt = 1700000000L.ToDateTimeFromUnixTimestamp();
DateTime? maybe = "1700000000".ToDateTimeFromUnixTimestamp();  // не null
```

### EnumerableExtensions

| Метод | Описание |
|--------|-------------|
| `JoinByString<T>(IEnumerable<T>, string?)` | Склеивает элементы через `string.Join`. Безопасно к `null`. |
| `JoinByString<T>(IEnumerable<T>, Func<T,T>, string?)` | Сначала преобразует, затем склеивает. Быстрый путь использует `ICollection<T>.Count` для предаллокации. |
| `JoinByString<T>(IEnumerable<T>, Func<T,int,T>, string?)` | Преобразование с индексом, затем склейка. |

```csharp
var csv = new[] { 1, 2, 3 }.JoinByString(", ");     // "1, 2, 3"
var joined = items.JoinByString(x => x.Name, "|");   // "Name1|Name2|..."
```

### ExceptionExtensions

| Метод | Описание |
|--------|-------------|
| `IsCritical(this Exception)` | Возвращает `true` для фатальных исключений CLR: `OutOfMemoryException`, `StackOverflowException`, `AppDomainUnloadedException`, `BadImageFormatException`, `CannotUnloadAppDomainException`, `InvalidProgramException`, `ThreadAbortException`. |
| `GetErrorMessages(this Exception)` | Склеивает все сообщения исключений от первопричины к внешнему, по одному на строку. |

```csharp
if (ex.IsCritical()) Environment.FailFast(ex.Message);
Console.WriteLine(ex.GetErrorMessages());
```

### SpanExtensions

| Метод | Описание |
|--------|-------------|
| `GetChunks<T>(Memory<T>, int)` | Перечисляет чанки `Memory<T>` через итератор. |
| `GetChunksArray<T>(Memory<T>, int)` | Материализованный `Memory<T>[]` с предварительно выделенной ёмкостью. |
| `SelectWhere<T,TResult>(Span<T>, Func<T,int,TResult>, Func<TResult,int,bool>?)` | Комбинированный Select+Where с индексом для `Span<T>`. Возвращает усечённый массив. |
| `SelectWhere<T,TResult>(Span<T>, Func<T,TResult>, Func<TResult,bool>?)` | То же без индекса. |
| `SelectWhere<T,TResult>(ReadOnlySpan<T>, ...)` | Перегрузки для `ReadOnlySpan<T>`. |

```csharp
var chunks = someMemory.GetChunksArray(256);       // Memory<byte>[]
var filtered = span.SelectWhere(x => x * 2, v => v > 10);
```

### StringExtensions

| Метод | Описание |
|--------|-------------|
| `NullIfEmpty(this string?)` | Возвращает `null`, если строка `null`, пуста или состоит только из пробелов. Иначе возвращает исходную. |
| `NormalizeWhiteSpace(bool isTrimmed)` | Схлопывает последовательные пробелы/разделители/управляющие символы в один пробел. Безаллокационный быстрый путь для «чистых» строк. |
| `NormalizeWhiteSpaceSpan(ReadOnlySpan<char>, Span<char>, bool)` | Span-вариант без аллокаций. Пишет в буфер назначения, возвращает длину. |
| `GetMurmurHash3(uint seed)` | Вычисляет MurmurHash3 UTF-8 представления без аллокации массива байтов. StackAlloc до 512 байт. |

```csharp
string? cleaned = "  hello   world  ".NormalizeWhiteSpace();  // "hello world"
uint hash = "key".GetMurmurHash3(seed: 42);
string? blank = "   ".NullIfEmpty();  // null
```

### StrToExtensions

Безопасные парсинг-расширения, возвращающие nullable-результат (`T?`) — никогда не бросают исключение на некорректном вводе.

| Метод | Тип входа | Возвращает |
|--------|-----------|---------|
| `StrToBool(string? / ReadOnlySpan<char>)` | строка / span | `bool?` |
| `StrToInt(string? / ReadOnlySpan<char>)` | строка / span | `int?` |
| `StrToShort(string? / ReadOnlySpan<char>)` | строка / span | `short?` |
| `StrToUShort(string? / ReadOnlySpan<char>)` | строка / span | `ushort?` |
| `StrToLong(string? / ReadOnlySpan<char>)` | строка / span | `long?` |
| `StrToULong(string? / ReadOnlySpan<char>)` | строка / span | `ulong?` |
| `StrToDouble(string? / ReadOnlySpan<char>)` | строка / span | `double?` |
| `StrToGuid(string? / ReadOnlySpan<char>)` | строка / span | `Guid?` |
| `StrToBytes(string, Encoding?)` | строка | `byte[]` (по умолчанию UTF-8) |
| `StrToEnum<T>(string?, T defaultValue)` | строка? | `T` (возвращает `defaultValue` при неудаче, case-insensitive) |
| `StrToDate(string? / ReadOnlySpan<char>, IFormatProvider?, DateTimeStyles)` | строка / span | `DateTime?` — перебирает ~60 форматов дат, включая ISO 8601 round-trip |

```csharp
int? port = "8080".StrToInt();              // 8080
Guid? id = "not-a-guid".StrToGuid();        // null
DateTime? dt = "2024-01-15".StrToDate();    // распарсено или null
string? mode = "red".StrToEnum("black");  // "red" (case-insensitive)
```

### JsonExtensions

| Метод | Описание |
|--------|-------------|
| `ToJson<T>(T, JsonSerializerOptions?)` | Сериализует в JSON-строку. |
| `FromJson<T>(string, JsonSerializerOptions?)` | Десериализует из JSON-строки. |

Оба метода помечены атрибутами `[RequiresUnreferencedCode]` и `[RequiresDynamicCode]` для предупреждений совместимости с AOT. См. `JsonHttpResultTrimmerWarning.SerializationUnreferencedCodeMessage` / `SerializationRequiresDynamicCodeMessage`.

---

## Типы диапазонов и секций (пространство имён `Sa.Classes`)

Интервальные типы для описания задержек повторов, размеров партиций и других ограниченных диапазонов.

### LimSection\<T\>

Замкнутый интервал `[min, max]`.

| Член | Описание |
|--------|-------------|
| Конструктор `(T min, T max)` | Создаёт замкнутый интервал. Бросает исключение, если `min > max`. |
| `Min` / `Max` | Границы интервала. |

### HalfSection\<T\>

Полуоткрытые интервалы: `OpenMin(min)` = `(min, ∞)` или `OpenMax(max)` = `(-∞, max]`.

| Член | Описание |
|--------|-------------|
| `Kind` | `OpenMin` или `OpenMax`. |
| `Bound` | Конечная граница. |

### Section\<T\>

Юнион-тип, оборачивающий `LimSection<T>` или `HalfSection<T>`. Единые методы расширения:

| Расширение | Описание |
|-----------|-------------|
| `Contains(Range<T>, T)` | Проверяет, попадает ли значение в секцию. |
| `Expand<T>(Section<T>, T, T)` | Расширяет секцию, включая новые границы. |
| `Shrink<T>(Section<T>, T, T)` | Сужает секцию. |
| `Center<T>(Section<T>)` | Середина `LimSection`. |
| `Width<T>(LimSection<T>)` | Расстояние между min и max. |
| `ApplyToBounds<T>(Section<T>, T, T)` | Приводит произвольные границы к секции. |
| `WithinBounds<T>(Section<T>, T, T)` | Проверяет, помещаются ли два значения внутрь секции. |
| `Overlaps<T>(Section<T>, Section<T>)` | Проверяет пересечение секций. |
| `MergeSections<T>(Section<T>, Section<T>)` | Создаёт секцию, покрывающую оба входа. |
| `GenerateValues<T>(Section<T>, int, Func<T, T, IEnumerable<T>>)` | Генерирует N значений, покрывающих секцию (для LINQ `Range`). |

```csharp
// Задержки повторов от 100ms до 5 секунд
Section<TimeSpan> delays = new LimSection<TimeSpan>(
    TimeSpan.FromMilliseconds(100),
    TimeSpan.FromSeconds(5));

bool inRange = delays.Contains(TimeSpan.FromSeconds(1)); // true
```

---

## Замечания по архитектуре

- Все типы **internal**, кроме явно помеченных публичных (`ProcessExecutionResult`, `ProcessExecutionException` и т.д.)
- Общий код распространяется паттерном `<Compile Include="..." Link="..." />` — подключается в проекты-потребители, а не поставляется как NuGet-пакеты
- Полная совместимость с **Native AOT** — без рефлексивной сериализации, без динамической генерации IL
- Ноль зависимостей: никаких внешних NuGet-пакетов
