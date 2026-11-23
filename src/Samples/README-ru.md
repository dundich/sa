# Быстрый старт — примеры

**Русский** | [English](README.md)

Набор запускаемых примеров, демонстрирующих каждую библиотеку из набора **Sa**.
Все примеры нацелены на **.NET 10.0**, большинство публикуется как **Native AOT**, и все следуют одному паттерну DI + Generic Host.

> **Инфраструктура:** база нужна только трём примерам — `Configuration.Web`, `Partitional.ConsoleApp` и `PgOutbox.ConsoleApp` используют PostgreSQL.
> Запустите её командой `docker-compose up -d db` (см. [`docker-compose.yml`](./docker-compose.yml)).

---

## Содержание

| # | Пример | Библиотека | Тип | Описание |
|---|--------|------------|-----|----------|
| 1 | [Configuration.Web](#1-configurationweb) | `Sa.Configuration` + `Sa.Configuration.PostgreSql` | Web API | Динамическая конфигурация из CLI-аргументов и таблицы PostgreSQL |
| 2 | [FFMpeg.Console](#2-ffmpegconsole) | `Sa.Media.FFmpeg` | Console | Проверка версии, список кодеков, конвертация MP3→WAV |
| 3 | [HybridFileStorage.Console](#3-hybridfilestorageconsole) | `Sa.HybridFileStorage` | Console | Гибридное хранилище файлов с абстракцией провайдеров |
| 4 | [Partitional.ConsoleApp](#4-partitionalconsoleapp) | `Sa.Partitional.PostgreSql` | Console | Декларативное партиционирование таблиц с расписанием миграций |
| 5 | [PgOutbox.ConsoleApp](#5-pgoutboxconsoleapp) | `Sa.Outbox.PostgreSql` | Console | Надёжная публикация сообщений через паттерн Outbox |
| 6 | [Schedule.Console](#6-scheduleconsole) | `Sa.Schedule` | Console | Планировщик задач с стратегиями обработки ошибок |
| 7 | [WorkQueue.Console](#7-workqueueconsole) | `Sa.Utils.WorkQueue` | Console | Очередь с ограниченным параллелизмом и динамическим изменением ёмкости на лету |
| 8 | [InMemoryOutbox.ConsoleApp](#8-inmemoryoutboxconsoleapp) | `Sa.Outbox` (in-memory) | Console | Outbox без базы, Docker и connection string |
| 9 | [Schedule.Configuration.Console](#9-scheduleconfigurationconsole) | `Sa.Schedule` | Console | Расписание и отключение задач из appsettings.json |
| 10 | [Echo](#10-echo) | `Sa.Media` | Console | Подавление общего сигнала двух каналов стерео-WAV |

---

## 1. Configuration.Web

Демонстрирует чтение конфигурации из аргументов командной строки и таблицы PostgreSQL, подаваемую как минимальный ASP.NET Core API.

### Что делает

1. Создаёт slim-ASP.NET приложение.
2. Парсит аргументы CLI через `Sa.Configuration.Arguments`.
3. Читает секреты (например, connection strings) из переменных окружения / файлов.
4. Инициализирует таблицу `settings` в PostgreSQL.
5. Hot-reload настроек из БД — изменения отражаются без перезапуска.
6. Выставляет `GET /settings` со всеми ключами конфигурации.

### Запуск

```powershell
# 1. Убедитесь, что PostgreSQL запущен
docker-compose up -d db

# 2. Установите connection string (или передайте как аргумент CLI)
$env:sa__pg__connection = "Host=localhost;Username=postgres;Password=postgres;Database=postgres"

# 3. Запуск
dotnet run --project Configuration.Web
```

### Тест

```powershell
curl http://localhost:5000/settings
```

Ожидаемый ответ:

```json
[
  { "key": "sa:pg:connection", "value": "Host=localhost;..." },
  { "key": "theme",       "value": "dark" },
  { "key": "language",    "value": "en" },
  { "key": "notifications","value": "enabled" },
  { "key": "secret",      "value": null }
]
```

---

## 2. FFMpeg.Console

Демонстрирует использование `Sa.Media.FFmpeg` для аудиообработки со встроенными бинарниками FFmpeg.

### Что делает

1. Получает версию FFmpeg.
2. Выводит список доступных кодеков.
3. Конвертирует `data/input.mp3` → `data/output.wav` (моно PCM_S16LE).

### Запуск

```powershell
dotnet run --project FFMpeg.Console
```

### Ожидаемый вывод

```
Hello, [Sa.Media.FFmpeg]!
ffmpeg version 6.x...
[aac, ac3, flac, ..., pcm_s16le, ...]
```

---

## 3. HybridFileStorage.Console

Демонстрирует `Sa.HybridFileStorage` — абстрагированный слой хранения файлов с автоматическим failover провайдера.

### Что делает

1. Регистрирует `InMemoryFileStorage` как основной провайдер.
2. Загружает текстовый файл (`"Hello, HybridFileStorage!"`).
3. Скачивает обратно и верифицирует содержимое.

### Запуск

```powershell
dotnet run --project HybridFileStorage.Console
```

### Ожидаемый вывод

```
starting
completed:Hello, HybridFileStorage!
```

---

## 4. Partitional.ConsoleApp

Демонстрирует декларативное партиционирование таблиц PostgreSQL с запланированными миграциями и очисткой.

### Что делает

1. Настраивает таблицу `customer`, партиционированную по списку (`country`, `city`).
2. Определяет расписание миграций для RU (Moscow, Samara), USA (Alabama, New York), FR (Paris, Lyon, Bordeaux).
3. Выполняет `partition.MigrateAsync()` для создания физических партиций.
4. Выводит список созданных партиций на следующие 3 дня.

### Запуск

```powershell
# 1. Убедитесь, что PostgreSQL запущен
docker-compose up -d db

# 2. Запуск (использует захардкоженный conn string)
dotnet run --project Partitional.ConsoleApp
```

### Ожидаемый вывод

```
Hello, Partitional.PostgreSql!
list of parts:
customer_20260701
customer_RU_Moscow_20260701
...
Successfully: True
```

---

## 5. PgOutbox.ConsoleApp

Демонстрирует паттерн Outbox для надёжной публикации сообщений с PostgreSQL-backed хранением.

### Что делает

1. Регистрирует две consumer группы: `Group1Consumer` (каждые 5с, одна итерация) и `RndConsumer` (каждые 25с, макс 2 попытки).
2. Публикует 3 начальных сообщения для tenant 1.
3. Фоновый сервис непрерывно публикует случайные сообщения для tenants 1–3.
4. Консьюмеры обрабатывают сообщения с разными исходами: Ok, Retry, Postpone, Warn, Abort, Error.

### Запуск

```powershell
# 1. Убедитесь, что PostgreSQL запущен
docker-compose up -d db

# 2. Запуск
dotnet run --project PgOutbox.ConsoleApp
```

### Ожидаемый вывод

```
Hello, Pg Outbox!
======= Group1Consumer : 1 =======
2026-07-01T... #123: Hi 1 [Ok]
2026-07-01T... #124: Hi 2 [Ok]
2026-07-01T... #125: Hi 3 [Ok]
======= RndConsumer : 1 =======
...
```

---

## 6. Schedule.Console

Демонстрирует `Sa.Schedule` — планировщик задач с стратегиями обработки ошибок.

### Что делает

1. Спрашивает, запускать как hosted service (Y/n).
2. Регистрирует `SomeJob`, который выполняется каждые 2 секунды с логикой retry при ошибке.
3. Добавляет interceptor, который логирует `<beg>` / `<end>` вокруг каждого выполнения.
4. Через 5с останавливает планировщик, ждёт 2с, затем перезапускает.
5. Через 30с отменяет всё.

### Запуск

```powershell
# Интерактивный: нажмите 'n' для standalone режима или 'y' для hosted service
dotnet run --project Schedule.Console
```

### Ожидаемый вывод (standalone режим)

```
Hello, Schedule! As host service (Y/n): n

<beg>
2026-07-01T... 0: Some 2
<end>
<beg>
2026-07-01T... 1: Some 2
<end>
err 0
err 1
*** stopped & start after 2 sec
<beg>
2026-07-01T... 0: Some 2
<end>
*** cancelled on timeout
*** THE END ***
```

---

## 7. WorkQueue.Console

Демонстрирует `Sa.Utils.WorkQueue` — очерёдную систему с ограниченным параллелизмом, back-pressure и динамическим изменением параллелизма на лету.

### Что делает

1. Создаёт `SaWorkChannel<int>` с начальным лимитом параллелизма 3 и максимумом 8.
2. Закладывает пачку из 10 элементов.
3. Пока элементы идут, меняет параллелизм с 3 на 6 и обратно на 2.
4. Ждёт завершения, выводит количество обработанных элементов.
5. Корректно завершает работу — завершающиеся элементы добираются, оставшиеся помечаются как `Faulted`.

### Запуск

```powershell
dotnet run --project WorkQueue.Console
```

### Ожидаемый вывод

```
=== Sa.WorkQueue sample ===
Enqueued 10 items, concurrency = 3
[8] #1 started
[8] #1 -> Running
...
Scaled up -> concurrency = 6
Scaled down -> concurrency = 2
Processed 10 items, queue is idle: True
Enabled after shutdown: False
*** THE END ***
```

---

## 8. InMemoryOutbox.ConsoleApp

Тот же конвейер publish → consume, что и в `PgOutbox.ConsoleApp`, но outbox живёт в памяти процесса — без базы, Docker и connection string.

### Что делает

1. Регистрирует in-memory провайдер через `AddSaOutboxUsingInMemory(...)`.
2. Публикует 4 сообщения для двух тенантов.
3. `PrintConsumer` получает их батчами и подтверждает каждое через `Ok()`.
4. Ждёт подтверждения всех 4 сообщений и останавливает хост.

Консьюмеры не знают, какой провайдер за ними стоит: чтобы перейти на долговечный вариант, достаточно заменить `AddSaOutboxUsingInMemory` на `AddSaOutboxUsingPostgreSql`.

### Запуск

```powershell
dotnet run --project InMemoryOutbox.ConsoleApp
```

### Ожидаемый вывод

```
In-memory Outbox — no database, no Docker.
info: InMemoryOutbox.PrintConsumer[1]
      [print] tenant 1 #1: 01 — Hi from tenant 1
      ...
All messages delivered. The store lives in process memory only — restart loses it.
```

---

## 9. Schedule.Configuration.Console

`Sa.Schedule`, где задачи объявлены в коде, а затем переопределяются по времени и отключаются из `appsettings.json` — конфигурация важнее кода, пересборка не нужна.

### Что делает

1. Объявляет три задачи в коде со значениями по умолчанию: `Heartbeat` каждые 30с, `SlowReport` каждые 2с, `NightlyCleanup` каждые 15 минут.
2. Вызывает `FromConfiguration("Schedule")`, и секция `Schedule:Jobs` переопределяет их:
   - `Heartbeat` → каждые 2с и запуск сразу при старте (`Immediate`);
   - `SlowReport` → `Disabled`;
   - `NightlyCleanup` → cron `0 3 * * *`.
3. Печатает связанные опции и работает 12 секунд, чтобы переопределение было видно.

### Запуск

```powershell
dotnet run --project Schedule.Configuration.Console
```

### Ожидаемый вывод

```
appsettings.json (Schedule:Jobs) — the code defaults above are overridden:
  Heartbeat        Immediate=True  Every=00:00:02
  SlowReport       Disabled=True
  NightlyCleanup   Cron=0 3 * * *
```

---

## 10. Echo

Подавление общего сигнала двух каналов на `Sa.Media`: из стереозаписи убирается то, что одинаково звучит в обоих каналах, — простой эффект «минус вокал».

### Что делает

1. Читает стерео-WAV (путь — первый аргумент).
2. Запускает `CrossFeedSeparator` с `AudioSeparationOptions`.
3. Пишет результат во временный `.wav`, печатает его размер и удаляет файл.

### Запуск

```powershell
# Из корня репозитория; без аргумента берётся
# src/Tests/Sa.MediaTests/data/pсmS16Le.wav
dotnet run --project src/Samples/Echo -- <path-to-stereo-wav>
```

Путь по умолчанию отсчитывается от корня репозитория, поэтому запускайте этот пример оттуда, а не из `src/Samples`.

### Ожидаемый вывод

```
Wrote suppressed output: <random>.wav
Output size: 96077 bytes
Result deleted.
```

---

## Остановка инфраструктуры

```powershell
docker-compose down
```
