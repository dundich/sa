# Sa — Набор инфраструктурных библиотек для .NET 10

Переиспользуемые инфраструктурные библиотек для .NET 10, совместимые с **Native AOT**, с включённым `nullable`, построенные на современных примитивах .NET.

> ⚠️ **Стабильность.** Все пакеты пока в major-version `0` (`0.x.y`), поэтому публичные API могут меняться ломающим образом между релизами без предупреждения — используйте на свой страх и риск.

---

## Библиотеки

### [Sa.Configuration](src/Libraries/Sa.Configuration) — Аргументы командной строки и секреты

`Arguments` — парсер аргументов командной строки с типизированными геттерами (`GetBool`, `GetInt`, `GetTimeSpan` и т.д.). `Secrets` — безопасное управление секретами из файлов, переменных окружения и host-key файлов с цепочками хранилищ и шаблонизацией `${secret:key}`.

---

### [Sa.Configuration.PostgreSql](src/Libraries/Sa.Configuration.PostgreSql) — Динамическая конфигурация из PostgreSQL

Добавляет источник конфигурации на основе PostgreSQL, поэтому изменения в базе применяются в приложении без редеплоя.


---

### [Sa.Data.S3](src/Libraries/Sa.Data.S3) — S3-клиент для данных

Minio-compatible S3-клиент для операций с данными.

---

### [Sa.Data.TempFolder](src/Libraries/Sa.Data.TempFolder) — Управление временными каталогами

`AddSaTempFolder` регистрирует ключевой `ITempFolder` (корневой каталог + стратегии): защищённые путём подпапки, отложенная фиксация активности записью, возрастная фоновая очистка с «спуском» по уровням, учёт объёма и контроль свободного места с edge-событиями, режим read-only и keyed-мультиинстансальную регистрацию.

---

### [Sa.Outbox.PostgreSql](src/Libraries/Sa.Outbox.PostgreSql) — Реализация на PostgreSQL

Реализация **Transactional Outbox** на PostgreSQL. Сообщения фиксируются в outbox-таблице вместе с бизнес-операциями в одной транзакции, затем доставляются с повторами и отслеживанием статуса. Потребление — конкурентное (`SKIP LOCKED`), координация оффсетов — advisory-замками. Применение — на вашем страхе и риске.

---

### [Sa.Partitional.PostgreSql](src/Libraries/Sa.Partitional.PostgreSql) — Декларативное партиционирование

Декларативное партиционирование таблиц PostgreSQL (range по дню/месяцу/году и list) с fluent-билдером, автоматической миграцией будущей партиций, очисткой по политике удержания и in-memory кэшем, автоматически инвалидируемым при runtime-изменениях.

---

### [Sa.Schedule](src/Libraries/Sa.Schedule) — Планировщик задач

Конфигурируемые задачи по расписанию с поддержкой cron-выражений, фиксированных интервалов и одноразовых запусков, со стратегиями ошибок на уровне задачи, повторами, лимитами параллелизма, интерцепторами и управлением старт/стоп/перезапуск на лету.

---

### [Sa.HybridFileStorage](src/Libraries/Sa.HybridFileStorage) — Мульти- провайдерное хранилище

`IHybridFileStorage` абстрагирует загрузку/выгрузку/удаление между несколькими провайдерами (FileSystem, S3, PostgreSQL) с автоматическим последовательным фейловером, пакетными операциями и интерцепторами на каждом провайдере. Провайдеры eagerly валидируют опции при регистрации, поэтому ошибка конфигурации проявляется рано, а не на каждой операции.

Првайдеры: [`Sa.HybridFileStorage.FileSystem`](src/Libraries/Sa.HybridFileStorage.FileSystem), [`Sa.HybridFileStorage.S3`](src/Libraries/Sa.HybridFileStorage.S3), [`Sa.HybridFileStorage.Postgres`](src/Libraries/Sa.HybridFileStorage.Postgres).

---

### [Sa.Media](src/Libraries/Sa.Media) — Асинхронное чтение WAV

Памятно-эффективный полностью асинхронный читатель WAV на базе `System.IO.Pipelines`: разбирает заголовок, читает сырые или нормализованные double-сэмплы по каналам, конвертирует между PCM16/24/32 и IEEE float, выдаёт потоковые чанки с настраиваемым размером батча.

---

### [Sa.Media.FFmpeg](src/Libraries/Sa.Media.FFmpeg) — Обёртка FFmpeg для .NET

Кроссплатформная обёртка над FFmpeg со встроенными статическими бинарниками (win-x64, linux-x64) — работает без установки. Включает извлечение метаданных, конвертацию аудио (PCM S16/S32 LE, F32 LE, raw, MP3, OGG), разделение/объединение каналов, потоковый ввод/вывод через стримы и DI.

---

### [Sa.Utils.WorkQueue](src/Libraries/Sa.Utils.WorkQueue) — Асинхронная очередь с ограничением параллелизма

Высокопроизводительная асинхронная очередь задач на `System.Threading.Channels` с ограниченной ёмкостью и back-pressure (стратегии заполнения при переполнении: `Wait`/`Skip`/`Throw`). Изменяемый на лету параллелизм (`ConcurrencyLimit`/`MaxConcurrency`) с динамическим масштабированием, порядком отмены ридеров (`Lifo`•`Fifo`•`RoundRobin`•`Random`) и режимами отмены (`Hard`/`Soft`). DI, безопасный (идемпотентный) shutdown, стратегии ошибок и обратные вызовы статусов.

---

## Примеры

В `src/Samples/`:

| Образец | Описание |
|---------|----------|
| [Configuration.Web](src/Samples/Configuration.Web) | CLI-аргументы + секреты в ASP.NET |
| [FFMpeg.Console](src/Samples/FFMpeg.Console) | Извлечение метаданных FFmpeg |
| [HybridFileStorage.Console](src/Samples/HybridFileStorage.Console) | Мульти- провайдерное хранилище |
| [Partitional.ConsoleApp](src/Samples/Partitional.ConsoleApp) | Декларативное партиционирование |
| [PgOutbox.ConsoleApp](src/Samples/PgOutbox.ConsoleApp) | Паттерн Outbox |
| [Schedule.Console](src/Samples/Schedule.Console) | Планировщик задач |
| [Schedule.Configuration.Console](src/Samples/Schedule.Configuration.Console) | Задачи по расписанию с подтягиванием настроек из appsettings.json |
| [Echo](src/Samples/Echo) | Разделение аудиоканалов на базе Sa.Media |
| [WorkQueue.Console](src/Samples/WorkQueue.Console) | Демо Sa.WorkQueue с изменением параллелизма на лету |

---

## Тесты

В `src/Tests/`: 17 тестовых проектов на **xunit v3** с **Testcontainers** (PostgreSQL + SeaweedFS) для интеграционных тестов.

---

## Сборка

```bash
# Полная сборка (clean + restore + build)
./build-sh/do-build.sh

# Запуск тестов
./build-sh/do-test.sh

# Создание NuGet-пакетов
./build-sh/do-package.sh
```

Прямые команды dotnet:

```bash
dotnet restore src/Sa.slnx -c Release
dotnet build src/Sa.slnx -c Release
```

Запускать `dotnet test` изнутри `src/`.

---

## Участие, спецификации и решения

Репозиторий развивается **spec-first**: требования и критерии приёмки — в [`specs/`](specs)
(см. [`specs/000-project-baseline`](specs/000-project-baseline)), шаблоны рабочих документов — в
[`templates/`](templates), гейты review — в [`checklists/`](checklists), ADR — в
[`docs/decisions/`](docs/decisions). Сам процесс описан в [`docs/README.md`](docs/README.md) и
[`docs/development/sdd-workflow.md`](docs/development/sdd-workflow.md); ограничения сборки/тестов/упаковки — в
[`AGENTS.md`](AGENTS.md).

---

## Архитектура

- Целевая платформа — **.NET 10.0** с **Native AOT**
- **Central Package Management** — версии в `Directory.Packages.props`
- Общие утилиты в **Sa** линкуются в consuming-проект через `<Compile Include="..." Link="..."/>`
- Все пакеты используют SDK-style csproj с implicit usings, nullable и анализаторами
- Решение управляется через `.slnx`
- Тесты на **xunit v3** с **Testcontainers** (PostgreSQL + SeaweedFS), изоляция на класс теста

## Лицензия

MIT
