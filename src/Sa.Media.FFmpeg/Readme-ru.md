# Sa.Media.FFmpeg

Кроссплатформенная обёртка .NET над FFmpeg (Windows x64, Linux) со **встроенными статическими бинарниками** — работает сразу без установки в систему. Упрощает обработку аудио: извлечение метаданных, конвертация форматов, разделение/объединение каналов и DI-интеграция.

---

## Возможности

- 🎵 **Извлечение метаданных** — длительность, битрейт, формат, частота дискретизации, каналы через `ffprobe`
- 🔊 **Конвертация аудио** — PCM S16 LE WAV, MP3, OGG Vorbis/Opus
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
Console.WriteLine($"Duration: {meta.Duration}s, Channels: {meta.Channels}");

// Конвертация аудио
await IFFMpegExecutor.Default.ConvertToPcmS16Le(
    "input.mp3",
    "output.wav",
    outputSampleRate: 16000,
    outputChannelCount: 1);

// Получение поддерживаемых форматов/кодеков
var formats = await IFFMpegExecutor.Default.GetFormats();
var codecs  = await IFFMpegExecutor.Default.GetCodecs();
```

### Разделение каналов (стерео → монофайлы)

```csharp
var splitter = new PcmS16LeChannelManipulator();

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
var merger = new PcmS16LeChannelManipulator();

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
| `ConvertToPcmS16LePreservingFormat(file, file, ...)` | `Task<string>` | Конвертация с сохранением формата |
| `ConvertToPcmS16Le(stream, func, ...)` | `Task` | Потоковая конвертация |
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
