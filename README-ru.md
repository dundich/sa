# Sa — Набор инфраструктурных библиотек для .NET 10

Нейрохерня - Серия переиспользуемых .NET 10-библиотек, сфокусированных на инфраструктурных паттернах для распределённых систем. Целевая платформа — **.NET 10.0**, используется **Native AOT**, применяется паттерн **Central Package Management (CPM)** через `Directory.Packages.props`.

---

### [Sa](src/Sa) — Общие утилиты

Ядро экосистемы **Sa**: базовые классы и методы расширения, линкуемые в другие пакеты через `<Compile Include="..." Link="..."/>`. Целевая платформа — **.NET 10.0**, совместимость с **Native AOT**, нулевые внешние зависимости.


Смотрите [полную документацию API](src/Sa/Readme.md).

---

### [Sa.Outbox.PostgreSql](src/Sa.Outbox.PostgreSql) — Реализация Outbox на PostgreSQL

Реализация паттерна **Transactional Outbox** на PostgreSQL для гарантированной доставки сообщений в распределённых системах. Предотвращает потерю сообщений и гарантирует обработку даже при сбоях.

- **Гарантированная доставка**: сообщения хранятся в БД до успешной обработки
- **Параллельная обработка**: несколько воркеров безопасно конкурируют за задачи через `SKIP LOCKED`
- **Мультитенантность**: изоляция и параллелизм по арендаторам
- **Авто-масштабирование**: runtime-изменение параллелизма без перезапуска
- **Планируемая очистка**: автоматическое удаление старых партиций
- **Self-bootstrapping**: авто-регистрация настроек консьюмера при первом запуске
- **Immutable настройки**: `OutboxConsumerSettings` record с fluent-билдером

See [full README](src/Sa.Outbox.PostgreSql/Readme.md).

---

### [Sa.Partitional.PostgreSql](src/Sa.Partitional.PostgreSql) — Декларативное партиционирование PostgreSQL

Объявление партицирования таблиц PostgreSQL (range: день/месяц/год; list) с автоматической миграцией, планированием очистки и in-memory кэшем.

- **Range-партиционирование** по дню, месяцу или году с авто-именованием по timestamp
- **List-партиционирование** по строковым/числовым ключам с иерархическими дочерними партициями
- **Fluent-билдер** для объявления таблиц, настройки fillfactor, кастомных ограничений и миграций
- **Автоматическая миграция** — предсоздание будущих партиций как фоновая задача
- **Автоматическая очистка** — удаление старых партиций по настраиваемому окну удержания
- **In-memory кэш** — избегает повторных запросов к каталогу; инвалидируется при runtime-изменениях
- **StrOrNum** — discriminated union для типобезопасных значений ключей партиций

See the full [Guide](src/Sa.Partitional.PostgreSql/Guide.md) and [API Reference](src/Sa.Partitional.PostgreSql/ApiReference.md).

---

### [Sa.Schedule](src/Sa.Schedule) — Планировщик задач

Конфигурация и выполнение задач по расписанию — cron, интервалы, одноразовые запуски.

| Возможность | Описание |
|-------------|----------|
| **Cron-тайминги** | Любое cron-выражение через `IJobTiming.FromCron()` |
| **Интервальное расписание** | `EverySeconds`, `EveryMinutes`, `EveryHours`, `EveryDays` |
| **Одноразовые задачи** | `RunOnce()` с опциональной начальной задержкой |
| **Стратегии ошибок** | `CloseApplication`, `AbortJob`, `StopAllJobs`, `Ignore` |
| **Повторные попытки** | Настраиваемое количество ретраев на ошибку |
| **Интерцепторы** | Кросс логика через `IJobInterceptor` |
| **Обработчики ошибок** | Глобальные `HandleError`-хендлеры на уровне планировщика |
| **DI-интеграция** | `AddSaSchedule(Action<IScheduleBuilder>)` с `BackgroundService` |

```csharp
builder.Services.AddSaSchedule(b => b
    .AddJob<MyCleanupJob>()
        .WithName("cleanup")
        .EveryHours(1)
        .ConfigureErrorHandling(eh => eh.IfErrorRetry(3).ThenAbortJob())
);
```

---

### [Sa.HybridFileStorage](src/Sa.HybridFileStorage) — Гибридное файловое хранилище

Абстракция файлового хранилища с автоматическим failover между провайдерами (FileSystem ↔ S3 ↔ PostgreSQL).

| Возможность | Описание |
|-------------|----------|
| **Мультипровайдер** | FileSystem, S3 (Minio), PostgreSQL — подключайте сколько угодно |
| **Автоматический failover** | При недоступности одного провайдера — переход к следующему |
| **Интерцепторы** | `before`, `after`, `onError` хуки на каждом провайдере |
| **Пакетные операции** | `CopyToScopeBatchAsync` с параллелизмом и прогрессом |
| **Расширения** | `CopyFromFileAsync`, `CopyToBasketAsync` для удобства |
| **InMemory-провайдер** | Для тестирования: `AddSaInMemoryFileStorage()` |

```csharp
builder.Services.AddSaHybridFileStorage(cfg => cfg
    .ConfigureStorage((sp, c) => c
        .AddStorage(new FileSystemStorage("disk"))
        .AddStorage(new S3Storage("s3"))
    )
);
```

---

### [Sa.Configuration](src/Sa.Configuration) — Аргументы командной строки и секреты

| Компонент | Назначение |
|-----------|------------|
| **Arguments** | Парсер CLI-аргументов в стиле dictionary — поддержка одиночных и множественных значений, типизированные геттеры (`GetBool`, `GetInt`, `GetTimeSpan` и т.д.) |
| **Secrets** | Безопасное управление секретами из файлов, переменных окружения и генерируемых host-key файлов. Поддержка chained stores и environment-aware загрузки |

```csharp
var args = Arguments.CreateDefault();
var dbPassword = args["db-password"]; // string?
var timeout = args.GetTimeSpan("timeout"); // TimeSpan?
```

---

### [Sa.Configuration.PostgreSql](src/Sa.Configuration.PostgreSql) — Динамическая конфигурация из PostgreSQL

Добавляет источник конфигурации из БД — изменения отражаются в приложении без перекомпиляции и редеплоя.

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    connectionString: "Host=localhost;Database=myapp",
    selectSql: "SELECT config_key, config_value FROM app_config",
    parameters: Array.Empty<NpgsqlParameter>()
));
```

---

### [Sa.Media](src/Sa.Media) — Асинхронное чтение WAV

Памятно-эффективный асинхронный читатель WAV-файлов с конвертацией форматов.

| Метод | Описание |
|-------|----------|
| `CreateFromFile(path)` | Открыть файл по пути |
| `GetHeaderAsync()` | Считать WAV-заголовок |
| `ReadSamplesPerChannelAsync()` | Потоковое чтение сэмплов по каналам |
| `ReadDoubleSamplesAsync()` | Нормализованные double-сэмплы [-1..1] |
| `ConvertToFormatAsync()` | Конвертация в PCM16/24/32, IEEE float |
| `ReadStreamableChunksAsync()` | Чанки фиксированного размера для streaming |

```csharp
using var reader = AsyncWavReader.CreateFromFile("audio.wav");
await foreach (var packet in reader.ReadDoubleSamplesAsync())
{
    Console.WriteLine($"Ch{packet.ChannelId}: {packet.Sample:F4}");
}
```

---

### [Sa.Media.FFmpeg](src/Sa.Media.FFmpeg) — Обёртка FFmpeg для .NET

FFmpeg из коробки со встроенными бинарниками (Windows x64 + Linux) и DI.

| Интерфейс | Назначение |
|-----------|------------|
| `IFFMpegExecutor` | Конвертация аудио/видео (PCM16LE, MP3, OGG) |
| `IFFProbeExecutor` | Извлечение метаданных (длина, каналы, частота, битрейт) |
| `IPcmS16LeChannelManipulator` | Разделение/объединение каналов |
| `IFFMpegLocator` | Автопоиск исполняемого FFmpeg |

```csharp
builder.Services.AddSaFFMpeg();

var probe = IFFProbeExecutor.Default;
var meta = await probe.GetMetaInfo("input.mp3");
Console.WriteLine($"Duration: {meta.Duration}s, Channels: {meta.Channels}");
```

---

### [Sa.Data.PostgreSql](src/Sa.Data.PostgreSql) — Лёгкая обёртка Npgsql

Без ORM-overhead, с DI, Native AOT и минимальными аллокациями.

| Метод | Описание |
|-------|----------|
| `ExecuteNonQuery` | INSERT / UPDATE / DELETE / DDL с возвратом rowCount |
| `ExecuteScalar / ExecuteScalarTyped<T>` | Одиночное значение с авто-кастомом |
| `ExecuteReader` | Потоковое чтение через callback (без загрузки в память) |
| `ExecuteReaderList<T>` | Сборка всех строк в `List<T>` |
| `ExecuteReaderFirst<T>` | Первое значение первого столбца |
| `ExecuteReaderSingle<T>` | Безопасное scalar-значение |
| `BeginBinaryImport` | Быстрый COPY BINARY для массового импорта |
| `PgRetryStrategy` | Повторы с jitter для transient-ошибок Npgsql |

---

### [Sa.Utils.WorkQueue](src/Sa.Utils.WorkQueue) — Асинхронная очередь с ограничением параллелизма

Высокопроизводительная очередь задач на базе `System.Threading.Channels` с ограниченной ёмкостью, динамическим контролем параллелизма и стратегиями масштабирования.

| Возможность | Описание |
|-------------|----------|
| **Ограниченная очередь** | Back-pressure через `BoundedChannel` |
| **Динамический параллелизм** | Изменяйте `ConcurrencyLimit` на лету |
| **Стратегии масштабирования** | `Lifo` • `Fifo` • `RoundRobin` • `Random` |
| **Стратегии ошибок** | `Continue`, `StopReader`, `ShutdownQueue` |
| **Обратные вызовы статусов** | `Running` → `Completed` / `Faulted` / `Cancelled` / `Aborted` |
| **Логирование без аллокаций** | `[LoggerMessage]` source generator |

See [full README](src/Sa.Utils.WorkQueue/Readme.md).

---

## Образцы

В `src/Samples/`:

| Образец | Описание |
|---------|----------|
| [Configuration.Web](src/Samples/Configuration.Web) | CLI-аргументы + секреты в ASP.NET |
| [FFmpeg.Console](src/Samples/FFmpeg.Console) | Извлечение метаданных FFmpeg |
| [HybridFileStorage.Console](src/Samples/HybridFileStorage.Console) | Мульти-провайдерное хранилище |
| [Partitional.ConsoleApp](src/Samples/Partitional.ConsoleApp) | Декларативное партиционирование |
| [PgOutbox.ConsoleApp](src/Samples/PgOutbox.ConsoleApp) | Паттерн Outbox |
| [Schedule.Console](src/Samples/Schedule.Console) | Планировщик задач |
| [Storage.Tests](src/Samples/Storage.Tests) | Тесты гибридного хранилища |

---

## Тесты

В `src/Tests/`: 15 тестовых проектов на **xunit v3** с **Testcontainers** (PostgreSQL + Minio) для интеграционных тестов.

---

## Сборка

```powershell
# Полная сборка
.\build\do_build.ps1

# Запуск тестов
.\build\do_test.ps1

# Создание NuGet-пакетов
.\build\do_package.ps1
```

Прямые команды dotnet:

```powershell
dotnet restore src/Sa.slnx -c Release
dotnet build src/Sa.slnx -c Release -v n
dotnet test src/Sa.slnx -v n
```

---

## Архитектура

- Целевая платформа — **.NET 10.0** с **Native AOT**
- **Central Package Management** — версии в `Directory.Packages.props`
- Общие утилиты в **Sa** линкуются в consuming-проект через `<Compile Include="..." Link="..."/>`
- Все пакеты используют SDK-style csproj с implicit usings, nullable и анализаторами
- Решение управляется через `.slnx`

## Лицензия

MIT
