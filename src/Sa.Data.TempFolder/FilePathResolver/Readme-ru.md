# FilePathResolver — Sa.Data.TempFolder

Компонент-спутник пакета: пространство имён `Sa.Data.TempFolder.FilePathResolver` живёт в своей
папке и состоит из двух файлов.

| Файл | Содержимое |
|---|---|
| `FilePathResolver.cs` | `IFilePathResolver` (контракт), `IFileSystemService` (шов к файловой системе), `DefaultFileSystemService` (внутренняя дисковая реализация шва) и класс `FilePathResolver` — сам резолвер. |
| `Setup.cs` | `Setup.AddFilePathResolver(...)` — регистрация в DI по ключу. |

## Назначение

Резолвер превращает нечёткие / неточные пути, которые хранятся в базе данных
(`/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/file.mp3`), в реальные пути файловой
системы (`/records/2021/06/16/15/file.mp3`).

Какую проблему он решает: путь в строке базы был записан на машине с уже несуществующей структурой
— диск смонтировали иначе, сменился корневой префикс, файл переехал под другой корень, либо
расширение отличается от указанного в строке. Обычный `File.Exists(dbPath)` на всё это отвечает
«нет»; резолвер вместо этого **ищет** файл под настроенным корнем и запоминает, как он нашёлся.

Компонент не зависит от самого temp-folder (ни `ITempFolder`, ни очистка, ни стратегия именования) —
только от файловой системы, и даже к ней идёт через шов (`IFileSystemService`), который можно
подменить фейком.

## Компоненты

| Тип | Видимость | Роль |
|---|---|---|
| `IFilePathResolver` | public | Тип сервиса для DI — единственный метод `ResolvePath(string dbPath, bool asRelativePath = false)`. |
| `FilePathResolver` | public | Реализация: каскад резолва, пофайловый кэш и флент-настройка (`WithMap`, `WithPossibleExtensions`, `WithDeepSearch`, `WithForceSearch`). |
| `IFileSystemService` | public | Шов к файловой системе — `FileExists`, `DirectoryExists`, `EnumerateFiles`. Весь доступ к диску идёт через него, поэтому резолвер тестируется без реальной файловой системы. |
| `DefaultFileSystemService` | internal | Дисковая реализация шва (`File` / `Directory`). При подмене шва регистрируется фейк. |

## Контракт — `IFilePathResolver`

```csharp
string? ResolvePath(string dbPath, bool asRelativePath = false);
```

- `dbPath` — путь в том виде, в каком он хранится в базе.
- `asRelativePath` — при `true` результат возвращается относительно `ConfiguredPath`, если
  найденный путь лежит под этим корнем.
- Возвращает найденный системный путь либо `null`, если файл найти не удалось. Отсутствующий файл
  — не исключение.

## Резолвер — `FilePathResolver`

`ResolvePath` прогоняет **каскад** — побеждает первый шаг, нашедший файл:

| # | Шаг | Что делает |
|---|-----|-----------|
| 1 | **Direct** | Путь из БД — абсолютный и буквально существует → используется как есть. |
| 2 | **Relative** | Путь не укоренён и существует под `ConfiguredPath` → `<root>/<dbPath>`. |
| 3 | **Incremental** | Завершающие компоненты каталога из БД сопоставляются с каталогами под `ConfiguredPath`, от внутреннего к внешнему: `/db/A/2024/a.mp3` → `<root>/A/2024/a.mp3`. Потерянный ведущий префикс всё равно резолвится. |
| 4 | **Deep search** (по включению) | Рекурсивный поиск по имени файла под `ConfiguredPath`. Совпадение не несёт маппинга каталога, поэтому оно **не кэшируется** и каждый вызов перечисляет дерево заново. |

Внутри одного вызова порядок такой: кэшированный пофайловый маппинг (если не включён принудительный
поиск) → явный маппинг `WithMap` → каскад.

Явный глобальный маппинг (`WithMap`) проверяется до каскада: путь из БД, начинающийся с
указанного префикса, ищется напрямую под `ConfiguredPath` (префикс отбрасывается, остаток —
относительный путь). Совпадение префикса проверяется по границе компонента: `/archive/usbdisk1` не
совпадает с `/archive/usbdisk10`.

```csharp
var resolver = FilePathResolver.Create("/records")
    .WithPossibleExtensions("mp3", "wav")
    .WithDeepSearch(true);

// "/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/has-root.mp3"
//   → /records/2021/06/16/15/has-root.mp3  (совпадение по суффиксу каталога)
string? full = resolver.ResolvePath(dbPath);
string? rel  = resolver.ResolvePath(dbPath, asRelativePath: true); // "2021/06/16/15/has-root.mp3"
```

### Кэш

Каждый найденный маппинг запоминается по пути из БД (`ConcurrentDictionary`), поэтому повторный
запрос по тому же пути — это чтение словаря и один файловый пробник вместо полного каскада.

- **Попадания кэшируются, промахи — нет**: появившийся позже файл найдёт следующий запрос.
- **Совпадения deep search не кэшируются** — найденный где угодно в дереве файл не несёт
  надёжного маппинга каталога.
- Кэшированный маппинг, который перестал резолвиться (файл переехал), ищется заново: каскад
  прогоняется ещё раз, и при включённом deep search файл находится на новом месте.
- `WithForceSearch(true)` заново прогоняет весь каскад на каждом вызове и перезаписывает кэш —
  полезно, когда структура меняется под работающим процессом.
- Кэш ограничен (65 536 запомненных путей): при переполнении он сбрасывается целиком, а не растёт
  с каждым новым путём БД, который видит долгоживущий процесс. Повторный резолв — это пара
  пробников, так что потеря дешёвая.

### Расширения

`WithPossibleExtensions("mp3", "wav")` задаёт расширения, которые перебираются, если точное имя
файла не найдено: резолвер сначала пробует `file.mp3`, затем `file.wav` (расширение без точки
эквивалентно). Список нормализуется (точка добавляется, дубликаты убираются) и доступен как
`PossibleExtensions`. Он же сужает deep search: при заданных расширениях подходят только файлы с
одним из них.

### Относительный результат

`ResolvePath(dbPath, asRelativePath: true)` возвращает путь относительно `ConfiguredPath`, если
найденный файл лежит под этим корнем (`2021/06/16/15/file.mp3`), и абсолютный путь без изменений,
если он лежит снаружи (буквально найденный путь).

### Событие `Resolved`

`Resolved` срабатывает **один раз** — при установке первого маппинга. Явный `WithMap` считается
маппингом и поднимает событие сразу; совпадение deep search — нет (маппинга там нет). `IsResolved`
сообщает то же состояние, но без события.

### Публичный контур

| Член | Описание |
|------|---------|
| `FilePathResolver.Create(string configuredPath)` | Создаёт резолвер поверх дисковой реализации шва по умолчанию. |
| `FilePathResolver.ConfiguredPath` | Корень, относительно которого идут все поиски (нормализован, без завершающего разделителя). |
| `FilePathResolver.WithMap(string map)` | Явный глобальный маппинг, применяемый до каскада. |
| `FilePathResolver.WithPossibleExtensions(params string[])` | Расширения, перебираемые при нечётком поиске. |
| `FilePathResolver.WithDeepSearch(bool)` | Включает / выключает рекурсивный поиск по имени файла. |
| `FilePathResolver.WithForceSearch(bool)` | Прогоняет каскад заново на каждом вызове вместо чтения кэша. |
| `FilePathResolver.PossibleExtensions` | Нормализованный список расширений (пуст, если ничего не задано). |
| `FilePathResolver.IsResolved` / `IsDeepSearch` / `IsForceSearchEnabled` | Текущее состояние резолвера. |
| `FilePathResolver.Resolved` (событие) | Срабатывает один раз — при установке первого маппинга. |
| `FilePathResolver.ResolvePath(string dbPath, bool asRelativePath = false)` | Резолвит путь из БД или возвращает `null`, если файл найти не удалось. Бросает `ArgumentException` при `null`/пробелах. |

## Шов к файловой системе — `IFileSystemService`

```csharp
bool FileExists(string path);
bool DirectoryExists(string path);
IEnumerable<string> EnumerateFiles(string directory, string pattern);
```

Весь доступ `FilePathResolver` к диску идёт через этот интерфейс, поэтому резолвер тестируется без
реальной файловой системы. `DefaultFileSystemService` — дисковая реализация по умолчанию
(`File.Exists`, `Directory.Exists`, `Directory.EnumerateFiles(..., SearchOption.AllDirectories)`),
она остаётся `internal`.

## Регистрация в DI — `Setup.cs`

```csharp
builder.Services.AddFilePathResolver("records", "/data/records",
    r => r.WithPossibleExtensions("mp3", "wav").WithDeepSearch(true));

// где-то дальше:
var resolver = sp.GetRequiredKeyedService<IFilePathResolver>("records");
string? full = resolver.ResolvePath(dbPath);
string? rel  = resolver.ResolvePath(dbPath, asRelativePath: true);
```

| Вызов | Описание |
|------|---------|
| `services.AddFilePathResolver(name, configuredPath, configure?)` | Регистрирует `IFilePathResolver` под ключом `name` — один хост может обслуживать несколько именованных корней с разными маппингами, расширениями и настройками поиска. |
| `services.AddFilePathResolver(configuredPath, configure?)` | То же под ключом по умолчанию `Setup.DefaultName` (`"default"`). |
| `sp.GetRequiredKeyedService<IFilePathResolver>(name)` | Резолвит инстанс (синглтон на ключ). Обычный `GetRequiredService<IFilePathResolver>()` keyed-сервисы не находит. |

- **Имена регистрации должны быть уникальны**: дубликат бросает `InvalidOperationException` при
  регистрации; `null`/пробелы в `name` или `configuredPath` → `ArgumentException`.
- **Регистрация ленивая**: инстанс строится при первом резолве, поэтому отсутствующий корень
  проявится как `DirectoryNotFoundException` там — но не при регистрации.
- `configure` выполняется один раз, при построении инстанса, и — та же флент-цепочка, что и при
  прямой сборке: `r => r.WithMap(...).WithPossibleExtensions(...).WithDeepSearch(true)`.
- **Шов к диску**: сначала keyed `IFileSystemService` (на конкретную регистрацию, по её имени),
  затем обычный, иначе — дисковая реализация по умолчанию:

```csharp
services.AddKeyedSingleton<IFileSystemService>("records", new FakeFileSystem());
```

## Ошибки

| Ситуация | Результат |
|----------|-----------|
| Путь из БД — `null` / пробелы | `ArgumentException` |
| Настроенный корень не существует | `DirectoryNotFoundException` при сборке — а значит, при первом резолве для DI-регистрации |
| Файл найти не удалось | `null` — не исключение |
| Имя регистрации уже занято | `InvalidOperationException` при регистрации |
| `name` / `configuredPath` — `null` или пробелы | `ArgumentException` при регистрации |

## Примечания

- Резолвер потокобезопасен: пофайловый кэш — `ConcurrentDictionary`, а событие `Resolved`
  срабатывает ровно один раз даже при конкурентных запросах. Исключение, брошенное обработчиком
  `Resolved`, проглатывается — подписчик не может сломать уже удачный резолв.
- Резолвер ничего не пишет на диск — он только его опрашивает.
- Пути нормализуются к разделителю платформы, завершающий разделитель отбрасывается.
