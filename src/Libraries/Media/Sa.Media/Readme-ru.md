# Sa.Media

**Русский** | [English](Readme.md)

Асинхронный, экономичный по памяти WAV-ридер для .NET 10+. Создан для совместимости с Native AOT с нулевыми аллокациями на горячих путях. Включает конвейер подавления кросс-феда для стерео (`Sa.Media.Echo`) для разделения близко расположенных источников.

## Установка

```bash
dotnet add package Sa.Media
```

---

## Содержание

- [Установка](#установка)
- [Возможности](#возможности)
- [Быстрый старт](#быстрый-старт)
  - [Чтение заголовка](#чтение-заголовка)
  - [Чтение сэмплов канала батчами](#чтение-сэмплов-канала-батчами)
  - [Чтение в другой кодировке](#чтение-в-другой-кодировке)
  - [Владение буфером](#владение-буфером)
  - [Байтовые смещения для временного диапазона](#байтовые-смещения-для-временного-диапазона)
- [Подавление кросс-феда (`Sa.Media.Echo`)](#подавление-кросс-феда-samediaecho)
- [Поддерживаемые форматы](#поддерживаемые-форматы)
- [Справочник публичного API](#справочник-публичного-api)
  - [Основные типы](#основные-типы)
  - [Типы Echo (`Sa.Media.Echo`)](#типы-echo-samediaecho)
  - [Ключевые методы `AsyncWavReader`](#ключевые-методы-asyncwavreader)
  - [`WavHeader`](#wavheader)
  - [Хелперы `TimeRange`](#хелперы-timerange)
- [Примечания по производительности](#примечания-по-производительности)
- [Структура проекта](#структура-проекта)
- [Лицензия](#лицензия)

## Возможности

- **Полностью асинхронный** — потоковая передача на основе `PipeReader`, без блокирующего I/O
- **Экономия памяти** — переиспользование буферов `ArrayPool`, один буфер на всю нумерацию
- **Мультиформатный вход** — PCM 8/16/24/32-bit, IEEE Float 32/64-bit, `WAVE_FORMAT_EXTENSIBLE`
- **Любая целевая кодировка** — `AudioEncoding.Pcm8/16/24/32`, `IeeeFloat32/64`; `PCM16 → PCM16` копируется напрямую, остальное идёт через нормализованное значение `[-1.0, 1.0]`
- **Учёт чанков** — `JUNK`, `LIST`, `bext` и любой другой чанк перед `data` пропускается, включая чанки нулевого размера
- **Без зависаний на хвосте** — неполный кадр на границе буфера не зацикливает `PipeReader`; хвост отбрасывается
- **Потоковые записи** — `data` размером `0xFFFFFFFF` восстанавливается из длины файла, если поток seekable
- **Подавление кросс-феда** — конвейер разделения стерео в `Sa.Media.Echo`

---

## Быстрый старт

### Чтение заголовка

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

var header = await reader.GetHeaderAsync(ct);
Console.WriteLine($"{header.NumChannels}ch @ {header.SampleRate}Hz, " +
    $"{header.BitsPerSample}-bit {header.AudioFormat}, {header.GetDuration()}");
```

`Create(Stream)` оставляет поток вызывающего открытым; `CreateFromFile` владеет `FileStream` и закрывает его.

### Чтение сэмплов канала батчами

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

await foreach (AudioChunk chunk in reader.ReadChannelChunksAsync(
    channel: 0,          // с нуля
    samplesPerBatch: 4096,
    cancellationToken: ct))
{
    // chunk.Samples  — сэмплы одного канала, `samplesPerBatch * bytesPerSample` байт
    // chunk.Position — байтовое смещение первого сэмпла от начала чанка `data`
    // chunk.IsEof    — true для последнего чанка (может быть короче samplesPerBatch)
}
```

Чанки собираются целыми кадрами, поэтому `Position` всегда выровнен по кадру, а нумерация
заканчивается там, где заканчивается `data`: следующие за ним чанки как аудио не читаются.

Нумерация канала читает пайп строго последовательно: перечислять канал 0 и канал 1
одновременно на одном ридере нельзя. Если нужны оба канала — откройте файл дважды
(так и делает конвейер Echo).

### Чтение в другой кодировке

```csharp
await foreach (AudioChunk chunk in reader.ReadChannelChunksAsync(
    channel: 1,
    samplesPerBatch: 1024,
    format: AudioEncoding.Pcm24BitSigned,   // по умолчанию Pcm16BitSigned
    cancellationToken: ct))
{
    // Сырой 24-битный little-endian PCM, 3 байта на сэмпл.
}
```

`PCM16 → PCM16` — прямое копирование. Любая другая пара конвертируется через нормализованное
значение `[-1.0, 1.0]`; целочисленные форматы клампятся (NaN становится тишиной), IEEE-float
сохраняет значения за пределами ±1.0 как есть.

### Владение буфером

`AudioChunk.Samples` ссылается на **пулированный** буфер, которым владеет нумерация:

- он действителен до следующего `MoveNextAsync()` (и до завершения нумерации);
- копируйте его, если данные должны жить дольше текущей итерации;
- выход из `await foreach` возвращает буфер в пул.

### Байтовые смещения для временного диапазона

Ридер всегда читает `data` с начала и не позиционируется. `WavHeader.CalculateCutOffsets`
даёт байтовые границы `TimeRange` внутри файла, чтобы вызывающий код с собственным I/O мог
обрезать точно по границам кадров:

```csharp
WavHeader header = await reader.GetHeaderAsync(ct);

// [начало, конец) в байтах для секунд 5–15, выровнено по кадрам и ограничено
// областью аудиоданных (для потоковых записей передайте fileSize).
(long from, long to) = header.CalculateCutOffsets(TimeRange.Seconds(5, 15), fileSize);
```

---

## Подавление кросс-феда (`Sa.Media.Echo`)

Разделяет стерео-запись двух близко расположенных микрофонов на две почти независимые
дорожки. Алгоритм оценивает акустическую утечку между каналами
(`y_L = x_L + β·x_R`, `y_R = x_R + α·x_L`) и инвертирует модель.

Две стратегии:

- **Linear** — только подавление кросс-феда (быстро, мало памяти). Три реализации: в памяти,
  потоковая с точным проходом за пиком, потоковая с быстрой оценкой пика.
- **Aggressive** — подавление кросс-феда плюс спектральное маскирование по частотам для более
  резкого разделения (выше CPU и память: запись загружается целиком, до ~100 M кадров).

```csharp
using Sa.Media.Echo;

// Автоматический режим линейного пути: короткие записи — в памяти, длинные — потоково.
await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
    InputPath: "recording.wav",
    OutputPath: "separated.wav"), CancellationToken.None);

// Aggressive со спектральным маскированием
await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
    InputPath: "recording.wav",
    OutputPath: "separated_aggressive.wav",
    Processing: CrossFeedSeparator.ProcessingMode.Aggressive,
    DominanceThresholdDb: 6.0,   // порог доминирования для участков с одним источником, дБ
    SpectralMaskPower: 4.0,      // показатель степени для обострения маски
    MaskFloor: 0.01),            // минимальный уровень маски [0, 1]
    CancellationToken.None);
```

- Вход обязан быть **2-канальным (стерео)** WAV; иначе `InvalidOperationException`.
- Выход — 16-битный PCM WAV, нормализованный по пику до 0.98.
- Если `OutputPath` не задан, результат пишется рядом с входом как
  `<имя>_linear.wav` / `<имя>_aggressive.wav`.
- `OutputPath` **не может указывать на сам входной файл**: потоковые режимы создают выход
  до того, как вход дочитан, поэтому вместо урезания записи бросается `ArgumentException`.
- Линейный путь выбирает режим по размеру входа: до 32 M кадров (~12 минут при 44.1 кГц)
  обрабатываются в памяти.

---

## Поддерживаемые форматы

| Формат | Чтение | Запись |
|--------|--------|--------|
| PCM 8-bit (unsigned) | ✅ | ✅ |
| PCM 16-bit (signed) | ✅ | ✅ |
| PCM 24-bit (signed) | ✅ | ✅ |
| PCM 32-bit (signed) | ✅ | ✅ |
| IEEE Float 32-bit | ✅ | ✅ |
| IEEE Float 64-bit | ✅ | ✅ |

Принимаются моно и стерео (до 6 каналов). `WAVE_FORMAT_EXTENSIBLE` читается, если его
`SubFormat` — PCM или IEEE float. Неизвестные чанки (`JUNK`, `LIST`, `bext`, …) между `fmt `
и `data` пропускаются — в том числе нулевого размера, — а отсутствующий `fmt ` репортится
как `InvalidDataException`, а не читается как данные.

Конвейер Echo читает любой из форматов выше и всегда пишет 16-битный PCM.

---

## Справочник публичного API

### Основные типы

| Тип | Описание |
|-----|----------|
| `AsyncWavReader` | Основной асинхронный ридер — создаётся из `Stream` или пути к файлу |
| `WavHeader` | Разобранный RIFF/WAV-заголовок с вычисляемыми свойствами (`IsPcm`, `IsStereo`, `GetDuration()`, …) |
| `AudioChunk` | Record: `(ReadOnlyMemory<byte> Samples, long Position, bool IsEof)` — сэмплы одного канала |
| `AudioEncoding` | Enum: PCM 8/16/24/32, IEEE Float 32/64 |
| `WaveFormatType` | Enum: `Pcm`, `Adpcm`, `IeeeFloat`, `Extensible` |
| `TimeRange` | Record: `(From, To)` с фабриками `Create`, `Ms`, `Seconds`, `RangeFromDuration`, `Default` |

### Типы Echo (`Sa.Media.Echo`)

| Тип | Описание |
|-----|----------|
| `CrossFeedSeparator` | Конвейер подавления кросс-феда (точка входа) |
| `CrossFeedSeparator.ProcessingMode` | Enum: `Auto`, `MaximumSpeed`, `MinimumMemory`, `MinimumMemoryFastNormalize`, `Aggressive` |
| `AudioSeparationOptions` | Record: `InputPath`, `OutputPath`, `Processing`, `DominanceThresholdDb`, `SpectralMaskPower`, `MaskFloor` |

### Ключевые методы `AsyncWavReader`

| Метод | Возвращает | Описание |
|-------|------------|----------|
| `Create(Stream, StreamPipeReaderOptions?)` | `AsyncWavReader` | Фабрика из потока; поток остаётся открытым (передайте `leaveOpen: false`, чтобы передать владение) |
| `CreateFromFile(string, FileStreamOptions?)` | `AsyncWavReader` | Фабрика из файла; ридер владеет `FileStream` |
| `GetHeaderAsync(ct)` | `Task<WavHeader>` | Потокобезопасный ленивый разбор заголовка; отмена первого вызова не «травит» ридер |
| `ReadChannelChunksAsync(channel, samplesPerBatch, format, ct)` | `IAsyncEnumerable<AudioChunk>` | Батчи сэмплов одного канала |
| `Dispose()` / `DisposeAsync()` | — | Идемпотентны; завершают `PipeReader` и закрывают принадлежащий файл |

### `WavHeader`

| Член | Описание |
|------|----------|
| `Validate()` | Бросает `NotSupportedException`/`InvalidDataException` для форматов и сочетаний полей, которые ридер не умеет |
| `GetDurationInSeconds(long? fileSize)` / `GetDuration()` | Длительность; для потоковых записей передайте длину файла, иначе `0` |
| `GetBytesPerSample()` / `SampleSize` | Байт на сэмпл одного канала (`BitsPerSample / 8`, `BlockAlign / NumChannels`) |
| `BlockAlign` / `GetBytesPerSecond()` | Байт на кадр / байт в секунду |
| `IsPcm`, `IsIeeeFloat`, `IsFloat`, `IsIntegerPcm`, `IsExtensible` | Формат сэмплов с учётом `SubFormat` у `WAVE_FORMAT_EXTENSIBLE` |
| `IsMono`, `IsStereo`, `NumChannels`, `SampleRate`, `BitsPerSample` | Раскладка |
| `HasDataSize`, `DataSize`, `DataOffset` | Чанк `data`; `HasDataSize == false` для потоковых записей |
| `CalculateCutOffsets(range, fileSize, alignToFrames)` | `(long cutFrom, long cutTo)` — байтовые границы `TimeRange` |
| `GetNormalizedConverter()` | `Func<ReadOnlySpan<byte>, double>` из сырого сэмпла в `[-1.0, 1.0]` |
| `ToString()` | Человекочитаемая многострочная сводка |

### Хелперы `TimeRange`

| Метод | Описание |
|-------|----------|
| `TimeRange.Create(from, to)` | Границы в `TimeSpan` |
| `TimeRange.Ms(from, to)` | Границы в миллисекундах; `long.MaxValue` — открытый конец |
| `TimeRange.Seconds(from, to)` | Границы в секундах; без `to` — открытый конец |
| `TimeRange.RangeFromDuration(from, duration)` | От начала плюс длительность |
| `TimeRange.Default` | `[0, ∞)` |
| `range.Contains(time)` | Включительное вхождение (по `TimeSpan`, `long` или `double` мс) |
| `Span<TimeRange>.Sort()` | Сортировка на месте по `From` |
| `ranges.Merge(thresholdMs)` | Слияние диапазонов, отстоящих меньше порога |
| `TimeRangeExtensions.ExpandTimeRanges(chunks, thresholdMs, gapMs)` | Слияние и растягивание соседних диапазонов с сохранением раскладки чанков |

---

## Примечания по производительности

- На всю нумерацию арендуется один выходной буфер (и один вспомогательный сэмпл) независимо от размера файла.
- Смещения и разбор кадров работают на `ReadOnlySequence<byte>` — без аллокаций на сэмпл и без `double`-прослойки для `PCM16 → PCM16`.
- Все внутренние await используют `ConfigureAwait(false)` — безопасно в любом контексте синхронизации.
- Ридер не позиционируется: для «обрезки» считайте смещения через `CalculateCutOffsets` и режьте в своём I/O.

---

## Структура проекта

```
src/Sa.Media/
├── AsyncWavReader.cs        # Ридер: заголовок, батчи по каналам
├── AudioChunk.cs            # Пулированный батч сэмплов одного канала
├── AudioEncoding.cs         # Enum целевой кодировки
├── AudioEncodingExtensions.cs
├── BinaryPipeReader.cs      # Little-endian читатель поверх PipeReader
├── PipeReaderExtensions.cs  # Хелпер пропуска байт
├── SampleConverter.cs       # PCM/float ↔ нормализованный double
├── TimeRange.cs             # Временной интервал
├── TimeRangeExtensions.cs   # Contains, Sort, Merge, ExpandTimeRanges
├── WavHeader.cs             # Модель RIFF-заголовка + смещения/длительность
├── WavHeaderReader.cs       # Разбор заголовка (с пропуском чанков)
├── WaveFormatType.cs        # Enum формата
└── WaveFormatTypeExtensions.cs

src/Sa.Media/Echo/
├── CrossFeedSeparator.cs     # Точка входа — оценка и инверсия кросс-тока
├── AudioSeparationOptions.cs # Публичные опции
├── AggressiveMode.cs         # Спектральное маскирование на STFT
├── AudioMath.cs              # Коэффициенты, перцентиль, нормализация
├── AudioConstants.cs         # Настраиваемые константы
└── WavIO.cs                  # Interleaved float I/O
```

---

## Лицензия

MIT
