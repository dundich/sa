# Sa.HybridFileStorage.FileSystem

Провайдер локальной файловой системы для `Sa.HybridFileStorage`. Хранит файлы как физические файлы на диске, делегируя чтение, запись и удаление экземпляру `Sa.Data.TempFolder`, которому принадлежат корень, защита путей, маркер активности очистки и политика повторов.

---

## Содержание

- [Обзор](#обзор)
- [Формат File ID](#формат-file-id)
- [Установка](#установка)
- [Быстрый старт](#быстрый-старт)
  - [Pre-инициализация (Configure)](#pre-инициализация-configure)
  - [Post-инициализация (PostConfigure)](#post-инициализация-postconfigure)
  - [Из конфигурации](#из-конфигурации)
  - [Несколько хранилищ в одном хосте](#несколько-хранилищ-в-одном-хосте)
- [Примеры CRUD](#примеры-crud)
- [Справочник опций](#справочник-опций)
- [Безопасность](#безопасность)
- [Обработка ошибок](#обработка-ошибок)
- [Миграция с `BasePath`](#миграция-с-basepath)

---

## Обзор

Провайдер файловой системы регистрирует `IFileStorage` поверх локальной файловой системы. Файлы хранятся в корне именованного экземпляра `Sa.Data.TempFolder` по структуре:

```
{RootPath}/{Basket}/{TenantId}/{FileName}
```

Сам провайдер лишь отображает File ID в этот относительный путь и формирует результат. Корень, защита путей, маркер активности очистки и повторы при преходящих ошибках ввода-вывода — во владении temp-folder: провайдер больше не открывает `FileStream` и не вызывает `File.Delete` напрямую.

Ключевые особенности:
- **Защита путей** — каждый путь разрешается относительно корня temp-folder; обход директорий и инъекционные символы отклоняются
- **Время жизни** — файлы живут под экземпляром temp-folder с очисткой по возрасту (по умолчанию 30 дней; см. ниже)
- **Преаллокация** — temp-folder преаллоцирует целевой файл, когда длина источника известна
- **Повторы** — преходящие `IOException` / `UnauthorizedAccessException` повторяются temp-folder

---

## Формат File ID

```
fs://{basket}/{tenantId}/{fileName}
```

**Примеры:**
- `fs://documents/42/report.pdf`
- `fs://uploads/7/avatar.png`
- `fs://share/100/data.bin`

> Примечание: слеши и обратные слеши в `FileName` санитизируются в разделитель платформы и очищаются от ведущих разделителей.

---

## Установка

```powershell
dotnet add package Sa.HybridFileStorage.FileSystem
```

---

## Быстрый старт

Провайдер регистрируется **по имени**, потому что его корень и политика ввода-вывода живут на temp-folder, зарегистрированном под тем же именем. В callback передаётся `IFileSystemStorageBuilder`: секция опций — через `FromConfiguration("…")`, стандартные `Configure` / `PostConfigure` / `Validate` — через `Options(...)`, а **обязательный** канал корня и ввода-вывода — `TempFolder(...)`:

```csharp
using Sa.HybridFileStorage.FileSystem;

builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(options => options.Basket = "documents"))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder =>
    {
        folder.RootPath = @"C:\data\files";
    }))));
```

Хранилище без канала `TempFolder(...)` выбрасывает `InvalidOperationException` прямо на вызове
`AddSaFileSystemFileStorage` — файловому хранилищу без корня некуда положить файл.

Конвейер опций хранилища выполняется в фиксированном порядке — **`Configure` → `PostConfigure` → валидация**, поэтому валидация видит уже нормализованные значения.

### Pre-инициализация (Configure)

`Configure` выполняется первым и получает «сырые» значения:

```csharp
builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(options => options.Basket = "documents"))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder => folder.RootPath = @"C:\data\files"))));
```

### Post-инициализация (PostConfigure)

`PostConfigure` выполняется после всех `Configure` и до валидации. Регистрация уже обрезает `StorageType` / `Basket`; всё, что добавите здесь, выполнится после этого и увидит уже нормализованные значения:

```csharp
builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(options => options.Basket = "documents")
        .PostConfigure(options => options.IsReadOnly = false))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder => folder.RootPath = @"C:\data\files"))));
```

### Из конфигурации

Передайте секции через `FromConfiguration` — опции хранилища и опции temp-folder привяжутся из `IConfiguration`:

```csharp
// appsettings.json
// {
//   "FileSystemStorage": { "Basket": "documents" },
//   "TempFolder":        { "RootPath": "C:\\data\\files", "MaxAge": "30.00:00:00" }
// }

builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .FromConfiguration("FileSystemStorage")
    .TempFolder(tb => tb.FromConfiguration("TempFolder")));
```

Привязка выполняется в фиксированном слоте **до** воспроизведения действий `Options(...)`,
поэтому при одновременном использовании последнее слово остаётся за их `Configure`.

### Несколько хранилищ в одном хосте

Каждый вызов регистрирует одно хранилище под своим именем; разные имена независимы, поэтому один
хост может обслуживать несколько файловых корней. Повтор имени выбрасывает
`InvalidOperationException` — имя ключует и хранилище, и его temp-folder
(`sp.GetRequiredKeyedService<ITempFolder>(name)`).

Пустой или отсутствующий `RootPath`, либо указывающий на системную временную директорию, приводит к
`OptionsValidationException` — на старте хоста (`ValidateOnStart()`) либо на первом разрешении
контейнера, собранного вручную, — а не при первой загрузке файла.

---

## Примеры CRUD

Ниже `storage` — это зарегистрированный `IFileStorage`, а `hybridStorage` — `IHybridFileStorage`
из ядра:

```csharp
var storage = sp.GetRequiredService<IFileStorage>();
```

### Загрузка из Stream

```csharp
using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, world!"));
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "hello.txt", TenantId = 1 },
    stream, ct);

// Файл создан: {RootPath}/documents/1/hello.txt
// File ID: fs://documents/1/hello.txt
```

### Загрузка из файла

Используйте `CopyFromFileAsync` из ядра `Sa.HybridFileStorage`:

```csharp
var result = await hybridStorage.CopyFromFileAsync(
    filePath: @"C:\temp\large-video.mp4",
    basket: "media",
    input: new UploadFileInput { FileName = "video.mp4", TenantId = 5 },
    bufferSize: 1024 * 1024,  // 1 MB буфер для больших файлов
    ct: ct);
```

### Скачивание в память

```csharp
byte[]? downloaded = default;
await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    downloaded = await stream.ReadAllBytesAsync(token);
}, ct);
```

### Скачивание на диск

```csharp
using var destination = new FileStream(@"C:\output\downloaded.pdf", FileMode.Create);
await storage.DownloadAsync(result.FileId, async (source, token) =>
    await source.CopyToAsync(destination, 81920, token),
    ct);
```

### Получение метаданных

```csharp
var metadata = await storage.GetMetadataAsync(result.FileId, ct);
if (metadata != null)
{
    Console.WriteLine($"Корзина: {metadata.Basket}");       // documents
    Console.WriteLine($"Тенант: {metadata.TenantId}");       // 42
    Console.WriteLine($"Имя: {metadata.FileName}");          // report.pdf
    Console.WriteLine($"Тип: {metadata.StorageType}");       // fs
}
```

### Удаление

```csharp
bool deleted = await storage.DeleteAsync(result.FileId, ct);
// true  — файл существовал и удалён
// false — файла нет, или преходящий сбой пережил все повторы temp-folder
```

---

## Справочник опций

### FileSystemStorageOptions

Один изменяемый тип, обслуживаемый конвейером options. Все свойства биндятся и устанавливаются, и
именно этот же экземпляр передаётся в хранилище — второго типа настроек и шага копирования,
который мог бы потерять свойство, больше нет. Корень хранилища и политика ввода-вывода переехали в
`TempFolderOptions` и доступны через обязательный канал `TempFolder(...)`.

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `Basket` | Имя контейнера, добавляемое к корню | `"share"` |
| `StorageType` | Префикс схемы в File ID | `"fs"` |
| `IsReadOnly` | Запрет операций записи/удаления | `false` |

`FileSystemStorageOptions.DefaultStorageType` и `FileSystemStorageOptions.DefaultBasket`
опубликованы как константы. Значения по умолчанию живут на самом типе, поэтому частичная
привязка конфигурации не затирает остальные свойства.

### TempFolderOptions (корень хранилища)

Канал `TempFolder(...)` настраивает именованный `ITempFolder`, через который хранилище читает и
пишет. Свойства, значимые для файлового хранилища:

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `RootPath` | Корневая директория хранилища; **не** должна быть пустой или системной временной директорией | `Path.GetTempPath()` (отклоняется) |
| `MaxAge` | Возраст, после которого стратегия очистки удаляет просроченную подпапку | **30 дней** (дефолт хранилища) |
| `TouchDebounce` | Задержка перед обновлением маркера активности папки после записи | 5 секунд |
| `OverwriteFiles` | Перезаписывать ли существующий файл при записи | `true` |

`Sa.HybridFileStorage.FileSystem` поднимает `MaxAge` хранилища с дефолтных для temp-folder 24 часов
до **30 дней**, потому что файлы хранилища должны жить дольше черновой папки. Значение из секции
или из `Configure` в коде всё равно побеждает.

Валидация выполняется после post-конфигурации и проверяет:

| Требование | К чему относится |
|------------|------------------|
| `StorageType` не длиннее 10 символов, без `:`, `/` и `\` | `StorageType` |
| `Basket` 3–63 символа, начинается с буквы или `_`, без разделителя пути | `Basket` |
| `RootPath` задан и не указывает на системную временную директорию | `TempFolderOptions` |

### Своя валидация

```csharp
builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(options => options.Basket = "documents")
        .Validate(options => options.StorageType == "fs", "Поддерживается только схема 'fs'."))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder => folder.RootPath = @"C:\data\files"))));
```

Ваше правило выполняется в дополнение к встроенным проверкам; все ошибки собираются вместе
в итоговом `OptionsValidationException`.

---

## Безопасность

Провайдер защищает от обхода директорий и инъекций через защиту путей temp-folder:

1. **Санитизация путей** — ведущие символы `/` или `\` в `FileName` удаляются; все разделители конвертируются в разделитель платформы
2. **Контейнирование корня** — каждый разрешённый путь файла должен попадать в `{RootPath}/{Basket}`. Попытки побега через `..` отклоняются с `SecurityException`
3. **Отклонение инъекций** — `~`, shell/glob-метасимволы (`< > | & ; ` $ ^ * ? " ' %`), управляющие и невидимые Unicode-символы отклоняются с `SecurityException`
4. **Детерминированные пути** — File ID отображаются в относительные пути без вычисления, предотвращая атаки через симлинки

```csharp
// Безопасно — нормализуется до "report.pdf"
new UploadFileInput { FileName = "/api/files/download/file/var/www/report.pdf" }
// Создаёт: {RootPath}/documents/1/report.pdf

// Заблокировано — обнаружен обход пути
// fileName = "../../../etc/passwd" → выбрасывается SecurityException

// Заблокировано — инъекционный символ (процент из query-строки)
// fileName = "file%name.txt" → выбрасывается SecurityException
```

---

## Обработка ошибок

| Сценарий | Поведение |
|----------|----------|
| `IsReadOnly = true` + загрузка/удаление | Выбрасывает `HybridFileStorageWritableException` |
| Файл не найден при скачивании/удалении | Возвращает `false` (без исключения) |
| Путь указывает на директорию при скачивании/удалении | Выбрасывает `InvalidOperationException` |
| IOException при удалении | Повторяется temp-folder; возвращает `false`, если все повторы неудачны |
| Попытка обхода пути или инъекции | Выбрасывает `SecurityException` |
| Неверный формат File ID | Выбрасывает `ArgumentException` |
| Невалидные опции | Выбрасывает `OptionsValidationException` на старте хоста (`ValidateOnStart`) или при первом разрешении |

---

## Миграция с `BasePath`

`FileSystemStorageOptions.BasePath` удалён: корень теперь живёт на temp-folder, за обязательным
каналом `TempFolder(...)`. Чтобы существующие File ID остались валидными, укажите в новом корне
прежнее значение:

```csharp
// Было
builder.Services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x =>
{
    x.BasePath = @"C:\data\files";
    x.Basket = "documents";
})));

// Стало
builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(x => x.Basket = "documents"))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder =>
        folder.RootPath = @"C:\data\files"))));
```

`FileSystemStorageOptions.BufferSize` тоже удалён — буфером копирования владеет temp-folder
(81920 байт). Регистрация стала только именованной: каждый существующий вызов
`AddSaFileSystemFileStorage(configure)` нуждается в имени и канале `TempFolder(...)`.

---

## Лицензия

MIT
