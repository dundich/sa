# Sa.HybridFileStorage.FileSystem

Провайдер локальной файловой системы для `Sa.HybridFileStorage`. Хранит файлы как физические файлы на диске с санитизацией путей, проверками безопасности и логикой повтора при преходящих ошибках ввода-вывода.

---

## Содержание

- [Обзор](#обзор)
- [Формат File ID](#формат-file-id)
- [Установка](#установка)
- [Быстрый старт](#быстрый-старт)
  - [Pre-инициализация (Configure)](#pre-инициализация-configure)
  - [Post-инициализация (PostConfigure)](#post-инициализация-postconfigure)
  - [Из конфигурации](#из-конфигурации)
  - [Одно хранилище на коллекцию](#одно-хранилище-на-коллекцию)
- [Примеры CRUD](#примеры-crud)
- [Справочник опций](#справочник-опций)
- [Безопасность](#безопасность)
- [Обработка ошибок](#обработка-ошибок)

---

## Обзор

Провайдер файловой системы регистрирует `IFileStorage` поверх локальной файловой системы. Файлы хранятся в настраиваемой корневой директории по структуре:

```
{BasePath}/{Basket}/{TenantId}/{FileName}
```

Ключевые особенности:
- **Санитизация путей** — предотвращает атаки через обход директорий
- **Умная преаллокация** — использует `FileStreamOptions.PreallocationSize`, когда длина потока известна
- **Повтор (retry)** — повторяет `IOException` при операциях удаления
- **Потоковое чтение/запись** — настраиваемый размер буфера для эффективности памяти

---

## Формат File ID

```
fs://{basket}/{tenantId}/{fileName}
```

**Примеры:**
- `fs://documents/42/report.pdf`
- `fs://uploads/7/avatar.png`
- `fs://share/100/data.bin`

> Примечание: слеши и обратные слеши в `FileName` санитизируются в прямые слеши и очищаются от ведущих разделителей.

---

## Установка

```powershell
dotnet add package Sa.HybridFileStorage.FileSystem
```

---

## Быстрый старт

Провайдер подключается через стандартный конвейер `Microsoft.Extensions.Options`. Метод возвращает
`IServiceCollection`, поэтому он складывается в цепочку с остальными вызовами `Add...`:

```csharp
using Sa.HybridFileStorage.FileSystem;

builder.Services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(options =>
{
    options.BasePath = @"C:\data\files";
    options.Basket = "documents";
})));
```

В callback передаётся `IFileSystemStorageBuilder`: секция — через `FromConfiguration("…")`,
стандартные методы `Configure` / `PostConfigure` / `Validate` — через `Options(...)`; отдельной
перегрузки под опции нет.

Конвейер выполняется в фиксированном порядке — **`Configure` → `PostConfigure` → валидация**,
поэтому валидация видит уже нормализованные значения.

### Pre-инициализация (Configure)

`Configure` выполняется первым и получает «сырые» значения:

```csharp
builder.Services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(options =>
{
    options.BasePath = @"C:\data\files";
    options.BufferSize = 512 * 1024;
})));
```

### Post-инициализация (PostConfigure)

`PostConfigure` выполняется после всех `Configure` и до валидации. Регистрация уже нормализует
`BasePath` в полный путь и обрезает `StorageType` / `Basket`; всё, что добавите здесь, выполнится
после этого и увидит уже нормализованные значения:

```csharp
builder.Services.AddSaFileSystemFileStorage(o => o
    .Options(ob => ob.Configure(options => options.BasePath = @"C:\data\files")
    .PostConfigure(options => options.BufferSize = 1024 * 1024)));
```

### Из конфигурации

Передайте секцию через `FromConfiguration` — опции будут привязаны из `IConfiguration`:

```csharp
// appsettings.json
// { "FileSystemStorage": { "BasePath": "C:\\data\\files", "Basket": "documents" } }

builder.Services.AddSaFileSystemFileStorage(b => b.FromConfiguration("FileSystemStorage"));
```

Привязка выполняется в фиксированном слоте **до** воспроизведения действий `Options(...)`,
поэтому при одновременном использовании последнее слово остаётся за их `Configure`.

### Одно хранилище на коллекцию

`AddSaFileSystemFileStorage` владеет экземпляром `FileSystemStorageOptions` без имени, поэтому
второй вызов выбрасывает `InvalidOperationException`. Регистрируйте файловый провайдер один раз,
а остальные корзины отдайте другим провайдерам.

Опции валидируются лениво, при первом разрешении, а `ValidateOnStart()` дополнительно форсирует
проверку при старте хоста. Поэтому пустой `BasePath`, некорректный `Basket` или неположительный
`BufferSize` приводят к `OptionsValidationException` — на старте хоста либо на первом разрешении
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

// Файл создан: {BasePath}/documents/1/hello.txt
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
// Возвращает false, если файл не существует
```

---

## Справочник опций

### FileSystemStorageOptions

Один изменяемый тип, обслуживаемый конвейером options. Все свойства биндятся и устанавливаются, и
именно этот же экземпляр передаётся в хранилище — второго типа настроек и шага копирования,
который мог бы потерять свойство, больше нет.

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `BasePath` | Корневая директория для всех файлов | *(обязательно)* |
| `Basket` | Имя контейнера, добавляемое к `BasePath` | `"share"` |
| `StorageType` | Префикс схемы в File ID | `"fs"` |
| `IsReadOnly` | Запрет операций записи/удаления | `false` |
| `BufferSize` | Размер буфера чтения/записи в байтах | `262144` (256 КБ) |

`FileSystemStorageOptions.DefaultStorageType` и `FileSystemStorageOptions.DefaultBasket`
опубликованы как константы. Значения по умолчанию живут на самом типе, поэтому частичная
привязка конфигурации не затирает остальные свойства.

Валидация выполняется после post-конфигурации и проверяет:

| Требование | К чему относится |
|------------|------------------|
| `BasePath` не null и не пустой | `BasePath` |
| `BasePath` — абсолютный путь, который можно создать | `BasePath` |
| `StorageType` не длиннее 10 символов, без `:`, `/` и `\` | `StorageType` |
| `Basket` 3–63 символа, начинается с буквы или `_`, без разделителя пути | `Basket` |
| `BufferSize` больше нуля | `BufferSize` |

Пустой `BasePath` намеренно **не** нормализуется в post-конфигурации: `Path.GetFullPath("   ")`
на Unix успешно отрабатывает и молча создал бы директорию с именем `"   "`.

### Своя валидация

```csharp
builder.Services.AddSaFileSystemFileStorage(o => o
    .Options(ob => ob.Configure(options => options.BasePath = @"C:\data\files")
    .Validate(options => options.BufferSize >= 64 * 1024, "BufferSize должен быть не меньше 64 КБ.")));
```

Ваше правило выполняется в дополнение к встроенным проверкам; все ошибки собираются вместе
в итоговом `OptionsValidationException`.

---

## Безопасность

Провайдер защищает от атак через обход директорий:

1. **Санитизация путей** — ведущие символы `/` или `\` в `FileName` удаляются; все обратные слеши конвертируются в прямые
2. **Контейнирование базового пути** — каждый разрешённый путь файла проверяется на принадлежность `{BasePath}/{Basket}`. Попытки побега через `../` отклоняются с `SecurityException`
3. **Детерминированные пути** — File ID отображаются в относительные пути без вычисления, предотвращая атаки через симлинки

```csharp
// Безопасно — нормализуется до "report.pdf"
new UploadFileInput { FileName = "/api/files/download/file/var/www/report.pdf" }
// Создаёт: {BasePath}/documents/1/report.pdf

// Заблокировано — обнаружен обход пути
// fileName = "../../../etc/passwd" → выбрасывается SecurityException
```

---

## Обработка ошибок

| Сценарий | Поведение |
|----------|----------|
| `IsReadOnly = true` + загрузка/удаление | Выбрасывает `HybridFileStorageWritableException` |
| Файл не найден при скачивании/удалении | Возвращает `false` (без исключения) |
| IOException при удалении | Повторяется внутренне; возвращает `false`, если все повторы неудачны |
| Попытка обхода пути | Выбрасывает `SecurityException` |
| Неверный формат File ID | Выбрасывает `ArgumentException` |
| Невалидные опции | Выбрасывает `OptionsValidationException` на старте хоста (`ValidateOnStart`) или при первом разрешении |

---


## Лицензия

MIT
