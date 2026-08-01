# Sa.Media.FFmpeg

Кроссплатформенная обёртка .NET над FFmpeg (Windows x64, Linux) со **встроенными статическими бинарниками** — работает сразу без установки в систему. Упрощает обработку аудио: извлечение метаданных, конвертация форматов, разделение/объединение каналов и DI-интеграция.

---

## Возможности

- 🎵 **Извлечение метаданных** — длительность, битрейт, формат, частота дискретизации, каналы через `ffprobe`
- 🔊 **Конвертация аудио** — PCM S16 LE WAV, PCM S32 LE WAV, сырой PCM S16 LE/F32 LE бинарник, MP3, OGG Vorbis/Opus
- 🎛️ **Манипуляция каналами** — разделение стерео на монофайлы, объединение двух моно в стерео
- 📦 **Встроенные бинарники FFmpeg** — Windows x64/arm64, Linux x64/arm64, macOS x64 (fallback на linux-x64)
- 💉 **Поддержка DI** — стандартная интеграция с `IServiceCollection` и конфигурацией опций
- ⚡ **Потоковый I/O** — передача аудио напрямую из потоков без промежуточных файлов

---

## Быстрый старт

### Дефолтные экземпляры (без настройки)

```csharp
using Sa.Media.FFmpeg;

// Извлечение метаданных
var meta = await IFFProbeExecutor.Default.GetMetaInfo("input.mp3");
Console.WriteLine($"Duration: {meta.Duration}s, Format: {meta.FormatName}");

// Получение каналов и частоты отдельно
var (channels, sampleRate) = await IFFProbeExecutor.Default.GetChannelsAndSampleRate("input.mp3");

// Конвертация аудио
await IFFMpegExecutor.Default.ConvertToPcmS16Le(
    "input.mp3",
    "output.wav",
    outputSampleRate: 16000,
    outputChannelCount: 1);

// PCM S32 LE WAV конвертация
await IFFMpegExecutor.Default.ConvertToPcmS32Le(
    "input.mp3",
    "output_s32le.wav",
    outputSampleRate: 48000,
    outputChannelCount: 2);

// Сырой float32 LE бинарник
await IFFMpegExecutor.Default.ConvertToPcmF32LeRaw(
    "input.mp3",
    "output_raw.f32le",
    outputSampleRate: 48000,
    outputChannelCount: 2);

// Конвертация с сохранением исходного формата
await IFFMpegExecutor.Default.ConvertToPcmS16LePreservingFormat(
    "input.mp3",
    "output_preserved.wav");

// Сырой PCM S16 LE бинарник (без WAV-заголовка)
await IFFMpegExecutor.Default.ConvertToPcmS16LeRaw(
    "input.mp3",
    "output_raw.s16le",
    outputSampleRate: 16000,
    outputChannelCount: 1);

// Получение версии FFmpeg / форматов / кодеков
var version = await IFFMpegExecutor.Default.GetVersion();
var formats = await IFFMpegExecutor.Default.GetFormats();
var codecs  = await IFFMpegExecutor.Default.GetCodecs();
```

### Разделение каналов (стерео → монофайлы)

Решите через DI (класс внутренний):

```csharp
// После вызова builder.Services.AddSaFFMpeg(...):
var services = builder.Services.BuildServiceProvider();
var splitter = services.GetRequiredService<IPcmS16LeChannelManipulator>();

var resultFiles = await splitter.SplitAsync(
    inputFileName: "stereo.mp3",
    outputFileName: "output",
    outputSampleRate: 16000,
    isOverwrite: true);

// Создаёт:
//   output_channel_0.wav  — левый канал
//   output_channel_1.wav  — правый канал
```

### Объединение каналов (моно → стерео)

```csharp
var merger = services.GetRequiredService<IPcmS16LeChannelManipulator>();

var joined = await merger.JoinAsync(
    leftFileName: "left.wav",
    rightFileName: "right.wav",
    outputFileName: "stereo_output.wav",
    outputSampleRate: 16000);
```

### Потоковая конвертация (без промежуточных файлов)

```csharp
await using var inputStream = File.OpenRead("input.mp3");

await IFFMpegExecutor.Default.ConvertToPcmS16Le(
    inputStream,
    inputFormat: "mp3",
    onOutput: async (stream, ct) =>
    {
        // Обрабатываем WAV-поток напрямую — например, подаём в AsyncWavReader
        await using var reader = new AsyncWavReader(stream);
        await foreach (var packet in reader.ReadDoubleSamplesAsync(ct))
        {
            Console.WriteLine($"Sample: {packet.Sample:F4}");
        }
    },
    outputSampleRate: 16000,
    outputChannelCount: 1);
```

### Сырая потоковая конвертация

```csharp
await using var inputStream = File.OpenRead("input.mp3");

await IFFMpegExecutor.Default.ConvertToPcmF32LeRaw(
    inputStream,
    inputFormat: "mp3",
    onOutput: async (rawStream, ct) =>
    {
        // Читаем сырой f32le бинарник — каждый float занимает 4 байта
        var buffer = new byte[4096];
        while (true)
        {
            var read = await rawStream.ReadAsync(buffer, ct);
            if (read == 0) break;
            // Обработка сырых float32 сэмплов...
        }
    },
    outputSampleRate: 48000,
    outputChannelCount: 2);
```

---

## С DI

```csharp
builder.Services.AddSaFFMpeg(configure: options =>
{
    options.ExecutablePath = @"C:\tools\ffmpeg.exe"; // опциональный override
    options.WritableDirectory = @"C:\temp\output";
    options.TimeoutSeconds = 300; // 5 минут
});

// Использование:
var executor = serviceProvider.GetRequiredService<IFFMpegExecutor>();
var probe    = serviceProvider.GetRequiredService<IFFProbeExecutor>();
var manip    = serviceProvider.GetRequiredService<IPcmS16LeChannelManipulator>();
```

Привязка секции конфигурации:

```csharp
builder.Services.AddSaFFMpeg(configSectionPath: "Ffmpeg");

// appsettings.json:
// {
//   "Ffmpeg": {
//     "ExecutablePath": "/usr/bin/ffmpeg",
//     "WritableDirectory": "/tmp/output",
//     "TimeoutSeconds": 300
//   }
// }
```

---

## Поддерживаемые конвертации

| Источник | Целевой | Метод | Примечание |
|----------|---------|-------|-----------|
| Любой, поддерживаемый FFmpeg | **PCM S16 LE WAV** | `ConvertToPcmS16Le()` | Настраиваемая частота (по умолч. 16 кГц), кол-во каналов |
| Любой | **PCM S16 LE WAV** | `ConvertToPcmS16LePreservingFormat()` | Сохраняет исходную частоту и каналы |
| Любой | **Сырой PCM S16 LE бинарник** | `ConvertToPcmS16LeRaw()` | Без WAV-заголовка, настраиваемая частота/каналы |
| Любой | **PCM S32 LE WAV** | `ConvertToPcmS32Le()` | 32-bit signed integer, настраиваемая частота/каналы |
| Любой | **PCM F32 LE WAV** | `ConvertToPcmF32Le()` | 32-bit IEEE float, настраиваемая частота/каналы |
| Любой | **Сырой PCM F32 LE бинарник** | `ConvertToPcmF32LeRaw()` | 32-bit IEEE float, без WAV-заголовка |
| Любой | **MP3** | `ConvertToMp3()` | 16 кГц, 128 kbps, libmp3lame |
| Любой | **OGG Vorbis** | `ConvertToOgg(isLibopus: false)` | Стандартный Vorbis |
| Любой | **OGG Opus** | `ConvertToOgg(isLibopus: true)` | Кодек Opus (только Linux) |

---

## Настройки

### FFMpegOptions

| Свойство | Тип | Описание | По умолчанию |
|----------|-----|----------|-------------|
| `ExecutablePath` | `string?` | Полный путь к бинарнику ffmpeg/ffprobe | Автопоиск (встроенный → PATH) |
| `WritableDirectory` | `string?` | Директория для выходных файлов | Текущая рабочая директория |
| `TimeoutSeconds` | `int?` | Таймаут операции в секундах | `300` (5 минут) |

Вызовите `options.Validate()` для проверки существования `WritableDirectory` и неотрицательности таймаута.

---

## Справочник публичного API

### IFFMpegExecutor

| Свойство/Метод | Возврат | Описание |
|----------------|---------|----------|
| `Default` | `IFFMpegExecutor` | Статический дефолтный экземпляр (использует встроенный бинарник) |
| `Executor` | `IFFRawExecutor` | Внутренний низкоуровневый процессор |
| `GetVersion()` | `Task<string>` | Строка версии FFmpeg |
| `GetFormats()` | `Task<string>` | Все поддерживаемые форматы |
| `GetCodecs()` | `Task<string>` | Все поддерживаемые кодеки |
| `ConvertToPcmS16Le(file, file, ...)` | `Task<string>` | Конвертация в WAV-файл |
| `ConvertToPcmS16Le(stream, func, ...)` | `Task` | Потоковая конвертация |
| `ConvertToPcmS16LePreservingFormat(file, file, ...)` | `Task<string>` | Конвертация с сохранением формата |
| `ConvertToPcmS16LeRaw(file, file, ...)` | `Task<string>` | Конвертация в сырой s16le бинарный файл |
| `ConvertToPcmS16LeRaw(stream, func, ...)` | `Task` | Потоковая конвертация в сырой s16le |
| `ConvertToPcmS32Le(file, file, ...)` | `Task<string>` | Конвертация в WAV (PCM S32 LE) |
| `ConvertToPcmF32Le(file, file, ...)` | `Task<string>` | Конвертация в WAV (PCM F32 LE) |
| `ConvertToPcmF32LeRaw(file, file, ...)` | `Task<string>` | Конвертация в сырой f32le бинарный файл |
| `ConvertToPcmF32LeRaw(stream, func, ...)` | `Task` | Потоковая конвертация в сырой f32le |
| `ConvertToMp3(file, file, ...)` | `Task<string>` | Конвертация в MP3 |
| `ConvertToOgg(file, file, ...)` | `Task<string>` | Конвертация в OGG (Vorbis или Opus) |

### IFFProbeExecutor

| Свойство/Метод | Возврат | Описание |
|----------------|---------|----------|
| `Default` | `IFFProbeExecutor` | Статический дефолтный экземпляр |
| `Executor` | `IFFRawExecutor` | Внутренний низкоуровневый процессор |
| `GetChannelsAndSampleRate()` | `Task<(int?, int?)>` | Сырая пара канал/частота |
| `GetMetaInfo(file)` | `Task<MediaMetadata>` | Полные метаданные из пути к файлу |
| `GetMetaInfo(stream, format)` | `Task<MediaMetadata>` | Полные метаданные из потока |

### IPcmS16LeChannelManipulator

| Метод | Возврат | Описание |
|-------|---------|----------|
| `SplitAsync(input, output, ...)` | `Task<IReadOnlyList<string>>` | Разделить стерео → несколько моно WAV |
| `JoinAsync(left, right, output, ...)` | `Task<string>` | Объединить два моно → стерео WAV |

### IFFRawExecutor

| Свойство/Метод | Возврат | Описание |
|----------------|---------|----------|
| `ExecutablePath` | `string` | Путь к бинарнику ffmpeg |
| `DefaultTimeout` | `TimeSpan` | Дефолтный таймаут операции |
| `ExecuteAsync(args, ...)` | `Task<ProcessExecutionResult>` | Выполнить FFmpeg с аргументами |
| `ExecuteStdOutAsync(args, stream, func, ...)` | `Task` | Пропустить stdin/stdout через FFmpeg |

---

## Доменные типы

### MediaMetadata

```csharp
public sealed record MediaMetadata(
    double? Duration = null,
    string? FormatName = null,
    int? BitRate = null,
    int? Size = null)
{
    public static readonly MediaMetadata Empty = new();
}
```

### ProcessExecutionResult

```csharp
public record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
```

---

## Исключения

| Исключение | Когда выбрасывается |
|------------|-------------------|
| `ProcessExecutionException` | FFmpeg завершается с ненулевым кодом |
| `ProcessExecutionResultException` | Обёртка над `ProcessExecutionResult` с форматированным сообщением |
| `ProcessStartException` | Не удалось запустить процесс FFmpeg |
| `ProcessTimeoutException` | Операция превысила таймаут |

---

## Встроенные бинарники

Статические сборки FFmpeg встраиваются на этапе билда и распаковываются в `sa/native/` во время выполнения. Установка в систему не требуется.

**Поддерживаемые RID:** `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` (macOS fallback на linux-x64).

**Порядок поиска:**
1. `AppContext.BaseDirectory/sa/native/ffmpeg`
2. `AppContext.BaseDirectory/ffmpeg`
3. Системный `PATH`

---

## Нативные зависимости (Linux)

Ubuntu/Debian:

```bash
sudo apt update && sudo apt install libmp3lame0 libopus0 libvorbis0a libvorbisenc2
```

Alpine Linux:

```bash
sudo apk add lame-libs opus libvorbis
```

---

## Структура проекта

```
src/Sa.Media.FFmpeg/
├── IFFMpegExecutor.cs           # Интерфейс конвертации аудио
├── IFFProbeExecutor.cs          # Интерфейс извлечения метаданных
├── IFFRawExecutor.cs            # Низкоуровневое выполнение процессов
├── IFFMpegExecutorFactory.cs    # Фабрика создания экzekторов
├── IFFMpegLocator.cs            # Поиск бинарников
├── IPcmS16LeChannelManipulator.cs # Операции split/join
├── FFMpegOptions.cs             # Опции конфигурации
├── MediaMetadata.cs             # DTO результата probe
├── Services/
│   ├── ProcessExecutor.cs       # Запускщик процессов + исключения
│   ├── FFMpegExecutor.cs        # Реализация
│   ├── FFProbeExecutor.cs       # Реализация
│   └── ...                      # Внутренние парсеры, сериализаторы
├── buildTransitive/
│   └── Sa.Media.FFmpeg.targets  # MSBuild: распаковка нативных бинарников
└── sa/                          # Локальные ZIP-архивы (только для разработки)
```

---

## Лицензия

MIT
