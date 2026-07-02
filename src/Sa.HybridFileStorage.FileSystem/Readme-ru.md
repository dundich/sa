# Sa.HybridFileStorage.FileSystem

Провайдер локальной файловой системы для `Sa.HybridFileStorage`. Хранит файлы как физические файлы на диске с санитизацией путей, проверками безопасности и логикой повтора при преходящих ошибках ввода-вывода.

---

## Содержание

- [Обзор](#обзор)
- [Формат File ID](#формат-file-id)
- [Установка](#установка)
- [Быстрый старт](#быстрый-старт)
  - [Без DI](#без-di)
  - [С DI](#с-di)
- [Примеры CRUD](#примеры-crud)
- [Справочник настроек](#справочник-настроек)
- [Безопасность](#безопасность)
- [Обработка ошибок](#обработка-ошибок)

---

## Обзор

`FileSystemStorage` реализует `IFileStorage` поверх локальной файловой системы. Файлы хранятся в настраиваемой корневой директории по структуре:

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

### Без DI

```csharp
using Sa.HybridFileStorage.FileSystem;
using Sa.HybridFileStorage.Domain;

var settings = new FileSystemStorageSettings
{
    BasePath = @"C:\data\files",
    Basket = "documents"
};

using var storage = new FileSystemStorage(settings);

// Загрузка
using var stream = File.OpenRead(@"C:\temp\document.pdf");
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "document.pdf", TenantId = 42 },
    stream, ct);

Console.WriteLine(result.FileId);  // fs://documents/42/document.pdf

// Скачивание
bool found = await storage.DownloadAsync(result.FileId, async (fs, token) =>
{
    using var reader = new StreamReader(fs, Encoding.UTF8);
    var content = await reader.ReadToEndAsync(token);
    Console.WriteLine(content);
}, ct);

// Удаление
bool deleted = await storage.DeleteAsync(result.FileId, ct);
```

### С DI

```csharp
using Sa.HybridFileStorage.FileSystem;

// Вариант 1: неизменяемые настройки (рекомендуется)
builder.Services.AddSaFileSystemFileStorage(new FileSystemStorageSettings
{
    BasePath = @"C:\data\files",
    Basket = "documents"
});

// Вариант 2: изменяемые опции с fluent builder
builder.Services.AddSaFileSystemFileStorage((sp, options) =>
{
    options.BasePath = @"C:\data\files";
    options.Basket = "documents";
    options.IsReadOnly = false;
    options.StorageType = "fs";
});
```

---

## Примеры CRUD

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

## Справочник настроек

### FileSystemStorageSettings (неизменяемые)

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `BasePath` | Корневая директория для всех файлов | *(обязательно)* |
| `Basket` | Имя контейнера, добавляемое к BasePath | `"share"` |
| `StorageType` | Префикс схемы в File ID | `"fs"` |
| `IsReadOnly` | Запрет операций записи/удаления | `false` |
| `BufferSize` | Размер буфера чтения/записи в байтах | `262144` (256 КБ) |

### FileSystemStorageOptions (изменяемые, fluent builder)

Используется с перегрузкой `Action<IServiceProvider, FileSystemStorageOptions>`:

| Свойство | Описание | По умолчанию |
|----------|----------|-------------|
| `BasePath` | Корневая директория для всех файлов | *(обязательно)* |
| `Basket` | Имя контейнера | `"share"` |
| `StorageType` | Префикс схемы в File ID | `"fs"` |
| `IsReadOnly` | Запрет операций записи/удаления | `false` |

Вызовите `options.Validate()` после конфигурации для проверки обязательных полей.

---

## Безопасность

`FileSystemStorage` защищает от атак через обход директорий:

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

---

## Лицензия

MIT
