# FFMpeg.Console

Минимальное консольное приложение, демонстрирующее работу **Sa.Media.FFmpeg** — обёртки над FFmpeg со встроенными бинарниками для Windows x64 и Linux, поддерживающей конвертацию аудио, проверку кодеков и извлечение метаданных.

---

## Быстрый старт

```bash
# Запустите пример напрямую
dotnet run --project Samples/FFMpeg.Console
```

Пример конвертирует `data/input.mp3` → `data/output.wav` (PCM S16 LE, моно, 16 кГц) и выводит информацию о версии + список кодеков.

> **Зависимости Linux:** На Ubuntu/Debian установите `libmp3lame0 libopus0 libvorbis0a libvorbisenc2`. На Alpine: `lame-libs opus libvorbis`.

---

## Что Демонстрирует Этот Пример

1. **Доступ к FFmpeg без конфигурации** — `IFFMpegExecutor.Default` автоматически находит бинарники через PATH или встроенную папку `sa/native/`.
2. **Кроссплатформенность** — Работает на Windows x64 и Linux из коробки.
3. **Конвертация аудио** — Конвертирует MP3 в WAV (PCM S16 LE) с настраиваемым количеством каналов и частотой дискретизации.
4. **Native AOT совместимость** — Публикуется с `PublishAot=true` и `InvariantGlobalization=true`.

---

## Ключевой Код

```csharp
using Sa.Media.FFmpeg;

Console.WriteLine("Hello, [Sa.Media.FFmpeg]!");
var ffmpeg = IFFMpegExecutor.Default;

// Проверить версию
var ver = await ffmpeg.GetVersion();
Console.WriteLine(ver.AsSpan(0, 21));

// Список всех поддерживаемых кодеков
var codecs = await ffmpeg.GetCodecs();
Console.WriteLine(codecs);

// Конвертировать MP3 → WAV (PCM S16 LE, моно)
await ffmpeg.ConvertToPcmS16Le(
    "data/input.mp3",
    "data/output.wav",
    outputChannelCount: 1);
```

---

## Полная Поверхность API

| Метод / Интерфейс | Назначение |
|-------------------|-----------|
| `IFFMpegExecutor.Default` | Singleton-экземпляр без DI |
| `GetVersion()` | Строка версии FFmpeg |
| `GetFormats()` | Список поддерживаемых форматов |
| `GetCodecs()` | Список поддерживаемых кодеков |
| `ConvertToPcmS16Le(path)` | Конвертация файла → WAV PCM S16 LE |
| `ConvertToPcmS16Le(stream, format, callback)` | Поточная конвертация |
| `ConvertToPcmS16LePreservingFormat()` | То же, но сохраняет оригинальную частоту/каналы |
| `ConvertToMp3()` | Конвертация → MP3 |
| `ConvertToOgg(libopus?)` | Конвертация → OGG (Vorbis или Opus) |
| `IFFProbeExecutor` | Извлечение метаданных через ffprobe |
| `GetMetaInfo(filePath)` | Длительность, битрейт, размер файла |
| `GetChannelsAndSampleRate(filePath)` | Количество каналов и частота дискретизации |
| `Setup.AddSaFFMpeg()` | Регистрация в DI-контейнере (Generic Host) |
| `FFMpegOptions` | Опции: путь к бинарнику, таймаут, рабочая директория |

---

## Использование с DI (Generic Host)

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSaFFMpeg();
var app = builder.Build();

var executor = app.Services.GetRequiredService<IFFMpegExecutor>();
await executor.ConvertToMp3("input.wav", "output.mp3");
```

---

## Файлы Проекта

| Файл | Путь |
|------|------|
| Исходный код | `Samples/FFMpeg.Console/Program.cs` |
| Проектный файл | `Samples/FFMpeg.Console/FFMpeg.Console.csproj` |
| Входной тестовый файл | `Samples/FFMpeg.Console/data/input.mp3` |

---

## Лицензия

MIT
