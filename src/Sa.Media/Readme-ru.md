# Sa.Media

Асинхронный, экономичный по памяти WAV-ридер для .NET 10+. Создан для совместимости с Native AOT с нулевыми аллокациями на горячих путях. Включает конвейер подавления кросс-феда для стерео (`Sa.Media.Echo`) для разделения близко расположенных источников.

---

## Возможности

- **Полностью асинхронный** — потоковая передача на основе `PipeReader`, без блокирующего I/O
- **Экономия памяти** — повторное использование буферов `ArrayPool`/`MemoryPool`, минимальная нагрузка на GC
- **Мультиформатная поддержка** — PCM 8/16/24/32-bit, IEEE Float 32/64-bit
- **Расширяемость** — поддерживает чанки `WAVE_FORMAT_EXTENSIBLE`
- **Обрезка по времени** — читайте только нужную часть через `TimeRange`
- **Канало-ориентированный** — перечисление сэмплов по каналам с отслеживанием позиции
- **Автоматический пропуск чанков** — `JUNK`, `LIST` и другие метаданные пропускаются прозрачно
- **Подавление кросс-феда** — конвейер разделения в `Sa.Media.Echo`

---

## Быстрый старт

### Чтение заголовка

```csharp
using var stream = File.OpenRead("test.wav");
var reader = new AsyncWavReader(stream);

var header = await reader.GetHeaderAsync();
Console.WriteLine($"{header.NumChannels}ch @ {header.SampleRate}Hz, " +
    $"{header.BitsPerSample}-bit {header.AudioFormat}");
```

### Чтение сырых сэмплов по каналам

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

await foreach (var packet in reader.ReadSamplesPerChannelAsync(
    cancellationToken: ct))
{
    Console.WriteLine($"Ch#{packet.ChannelId}: {packet.Sample.Length} bytes at pos {packet.Position}");
}
```

### Чтение нормализованных double сэмплов [-1.0 … 1.0]

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

await foreach (var packet in reader.ReadDoubleSamplesAsync(cancellationToken: ct))
{
    Console.WriteLine($"Ch#{packet.ChannelId}: {packet.Sample:F4}");
}
```

### Потоковые батчи (идеально для аудио-пайплайнов)

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

await foreach (var batch in reader.ReadStreamableChunksAsync(
    samplesPerBatch: 4096,
    cancellationToken: ct))
{
    // Каждый yield создаёт независимые данные — безопасно обрабатывать асинхронно
}
```

### Обрезка по временному диапазону

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

// Читаем только секунды 5–15
var range = TimeRange.Seconds(5, 15);
await foreach (var packet in reader.ReadDoubleSamplesAsync(range, cancellationToken: ct))
{
    // Только сэмплы из обрезанного диапазона
}
```

### Конвертация в другой формат

```csharp
await using var reader = AsyncWavReader.CreateFromFile("input.wav");

// Конвертация в 24-bit PCM
await foreach (var packet in reader.ConvertToFormatAsync(
    AudioEncoding.Pcm24BitSigned,
    cancellationToken: ct))
{
    // Сырые 24-битные PCM байты на каждый семпл
}
```

---

## Подавление кросс-феда (`Sa.Media.Echo`)

Разделяет стерео-запись двух близко расположенных говорящих/микрофонов на два
почти раздельных трека. Алгоритм оценивает акустический кросс-фед между
каналами (`y_L = x_L + β·x_R`, `y_R = x_R + α·x_L`) и инверсирует модель.

Поддерживает два режима:

- **Линейный** — только отмена кросс-феда (быстро, мало памяти).
- **Агрессивный** — отмена кросс-феда плюс частотное спектральное маскирование
  для более резкого разделения (выше нагрузка на CPU/GPU).

```csharp
using Sa.Media.Echo;

// Режим по умолчанию: в памяти для коротких клипов, потоково для длинных
await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
    InputPath: "recording.wav",
    OutputPath: "separated.wav"), CancellationToken.None);

// Агрессивный режим со спектральным маскированием
await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
    InputPath: "recording.wav",
    OutputPath: "separated_aggressive.wav",
    Processing: CrossFeedSeparator.ProcessingMode.Aggressive,
    DominanceThresholdDb: 6.0,   // порог в дБ для определения областей одного говорящего
    SpectralMaskPower: 4.0,      // экспонента усиления спектральной маски
    MaskFloor: 0.01),            // минимум маски [0, 1]
    CancellationToken.None);
```

Разделитель должен работать с **2-канальным (стерео)** записью. Вывод —
16-bit PCM WAV с нормализацией по пиковому уровню. Линейный путь работает в
потоковом режиме и не загружает весь файл в память; режим в памяти
автоматически включается для коротких записей (≤ 100 Mframes).

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

Все форматы поддерживают моно и стерео. Неизвестные чанки (`JUNK`, `LIST` и т.д.) автоматически пропускаются.

---

## Справочник публичного API

### Основные типы

| Тип | Описание |
|-----|----------|
| `AsyncWavReader` | Главный асинхронный WAV-ридер — создаётся из `Stream` или пути к файлу |
| `WavHeader` | Разобранный RIFF/WAV заголовок с вычисляемыми свойствами (`IsPcm`, `IsStereo`, `Duration`) |
| `AudioPacket` | Record: `(ChannelId, Sample, Position, IsEof)` — сырые/конвертированные байты |
| `AudioNormalizedPacket` | Record: `(ChannelId, Sample, Position, IsEof)` — нормализованные double [-1.0, 1.0] |
| `TimeRange` | Record: `(From, To)` — обрезка по времени с фабричными методами |
| `AudioEncoding` | Enum: PCM 8/16/24/32, IEEE Float 32/64 |
| `WaveFormatType` | Enum: `Pcm`, `Adpcm`, `IeeeFloat`, `Extensible` |

### Типы Echo (`Sa.Media.Echo`)

| Тип | Описание |
|-----|----------|
| `CrossFeedSeparator` | Конвейер подавления кросс-феда для стерео (главная точка входа) |
| `CrossFeedSeparator.ProcessingMode` | Enum: `Auto`, `MaximumSpeed`, `MinimumMemory`, `MinimumMemoryFastNormalize`, `Aggressive` |
| `AudioSeparationOptions` | Record: `InputPath`, `OutputPath`, `Processing`, `DominanceThresholdDb`, `SpectralMaskPower`, `MaskFloor` |

### Ключевые методы `CrossFeedSeparator`

| Метод | Возврат | Описание |
|-------|---------|----------|
| `ExecuteAsync(AudioSeparationOptions, ct)` | `Task` | Выполняет разделение и записывает вывод в WAV |

### Ключевые методы `AsyncWavReader`

| Метод | Возврат | Описание |
|-------|---------|----------|
| `Create(Stream)` | `AsyncWavReader` | Фабрика из потока |
| `CreateFromFile(string)` | `AsyncWavReader` | Фабрика из пути к файлу |
| `GetHeaderAsync()` | `Task<WavHeader>` | Потокобезопасное ленивое разбирание заголовка |
| `ReadSamplesPerChannelAsync()` | `IAsyncEnumerable<AudioPacket>` | Сырые сэмплы по каналам |
| `ReadDoubleSamplesAsync()` | `IAsyncEnumerable<AudioNormalizedPacket>` | Нормализованные double сэмплы |
| `ConvertToFormatAsync()` | `IAsyncEnumerable<AudioPacket>` | Конвертация в целевую кодировку |
| `ReadStreamableChunksAsync()` | `IAsyncEnumerable<AudioPacket>` | Пакетные сэмплы для пайплайнов |

### Фабрики `TimeRange`

| Метод | Пример | Описание |
|-------|--------|----------|
| `TimeRange.Create(from, to)` | Базовый конструктор | From/to TimeSpan |
| `TimeRange.Ms(from, to)` | В миллисекундах | Точность до мс |
| `TimeRange.Seconds(from, to)` | В секундах | Точность до double секунд |
| `TimeRange.RangeFromDuration(from, dur)` | От начала + длительность | Построение от смещения |
| `TimeRange.Default` | `[0, ∞)` | Весь файл, без обрезки |

---

## Примечания по производительности

- `allowBufferReuse=true` (по умолчанию) повторно использует пуленные буферы между yields — вызывающий должен скопировать перед следующей итерацией
- `allowBufferReuse=false` выделяет новый массив на каждый семпл — безопаснее для параллельных потребителей
- `ReadStreamableChunksAsync` принудительно устанавливает `allowBufferReuse:false` внутри для предотвращения алиасинга буферов
- Все внутренние await используют `ConfigureAwait(false)` — безопасно в любом синхронизационном контексте

---

## Структура проекта

```
src/Sa.Media/
├── AsyncWavReader.cs        # Основной класс ридера
├── AsyncWavWriter.cs        # Внутренний WAV-писатель
├── AudioEncoding.cs         # Enum форматов
├── AudioEncodingExtensions.cs
├── AudioPacket.cs           # Record сырого семпла
├── AudioNormalizedPacket.cs # Record нормализованного семпла
├── BinaryPipeReader.cs      # Little-endian бинарный ридер
├── PipeReaderExtensions.cs  # Помощники пропуска
├── SampleConverter.cs       # Конвертация PCM ↔ double
├── TimeRange.cs             # Диапазон обрезки
├── TimeRangeExtensions.cs   # Расширение, объединение, сортировка
├── WavHeader.cs             # Модель RIFF заголовка
├── WavHeaderReader.cs       # Парсер заголовка
├── WaveFormatType.cs        # Enum типа формата
└── WaveFormatTypeExtensions.cs

src/Sa.Media/Echo/
├── CrossFeedSeparator.cs     # Точка входа — оценка + инверсия кросс-феда
├── AudioSeparationOptions.cs # Публичный record опций
├── AggressiveMode.cs         # Спектральное маскирование на основе STFT
├── AudioMath.cs              # Математика коэффициентов, перцентиль, нормализация
├── AudioConstants.cs         # Настраиваемые константы
└── WavIO.cs                  # Помощники ввода-вывода чередованных float
```

---

## Лицензия

MIT
