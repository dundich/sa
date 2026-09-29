# Sa — Набор инфраструктурных библиотек для .NET 10

Переиспользуемые инфраструктурные библиотек для .NET 10, совместимые с **Native AOT**, с включённым `nullable`, построенные на современных примитивах .NET.

---

## Библиотеки

### [Sa.Configuration](src/Sa.Configuration) — Аргументы командной строки и секреты

`Arguments` — словарноподобный парсер аргументов командной строки с типизированными геттерами (`GetBool`, `GetInt`, `GetTimeSpan` и т.д.). `Secrets` — безопасное управление секретами из файлов, переменных окружения и host-key файлов с цепочками хранилищ и шаблонизацией `${secret:key}`.

---

### [Sa.Configuration.PostgreSql](src/Sa.Configuration.PostgreSql) — Динамическая конфигурация из PostgreSQL

Добавляет источник конфигурации на основе PostgreSQL, поэтому изменения в базе применяются в приложении без редеплоя.

---

### [Sa.Data.PostgreSql](src/Sa.Data.PostgreSql) — Лёгкая обёртка Npgsql

Тонкая обёртка над Npgsql, дружелюбная к Native AOT, для типовых операций с базой данных без нагрузки ORM: не-выборочное выполнение, скаляры, потоковые читатели, транзакции, бинарный COPY-импорт...

---

### [Sa.Data.S3](src/Sa.Data.S3) — S3-клиент для данных

Minio-compatible S3-клиент для операций с данными.

---

### [Sa.Outbox.PostgreSql](src/Sa.Outbox.PostgreSql) — Реализация на PostgreSQL

Реализация **Transactional Outbox** на PostgreSQL. Сообщения фиксируются в outbox-таблице вместе с бизнес-операциями в одной транзакции, затем доставляются с повторами и отслеживанием статуса. Потребление — конкурентное (`SKIP LOCKED`), координация оффсетов — advisory-замками. Применение — на вашем страхе и риске.

---

### [Sa.Partitional.PostgreSql](src/Sa.Partitional.PostgreSql) — Декларативное партиционирование

Декларативное партиционирование таблиц PostgreSQL (range по дню/месяцу/году и list) с fluent-билдером, автоматической миграцией будущей партиций, очисткой по политике удержания и in-memory кэшем, автоматически инвалидируемым при runtime-изменениях.

---

### [Sa.Schedule](src/Sa.Schedule) — Планировщик задач

Конфигурируемые задачи по расписанию с поддержкой cron-выражений, фиксированных интервалов и одноразовых запусков, со стратегиями ошибок на уровне задачи, повторами, лимитами параллелизма, интерцепторами и управлением старт/стоп/перезапуск на лету.

---

### [Sa.HybridFileStorage](src/Sa.HybridFileStorage) — Мульти- провайдерное хранилище

`IHybridFileStorage` абстрагирует загрузку/выгрузку/удаление между несколькими провайдерами (FileSystem, S3, PostgreSQL) с автоматическим последовательным фейловером, пакетными операциями и интерцепторами на каждом провайдере. Провайдеры eagerly валидируют опции при регистрации, поэтому ошибка конфигурации проявляется рано, а не на каждой операции.

Првайдеры: [`Sa.HybridFileStorage.FileSystem`](src/Sa.HybridFileStorage.FileSystem), [`Sa.HybridFileStorage.S3`](src/Sa.HybridFileStorage.S3), [`Sa.HybridFileStorage.Postgres`](src/Sa.HybridFileStorage.Postgres).

---

### [Sa.Media](src/Sa.Media) — Асинхронное чтение WAV

Памятно-эффективный полностью асинхронный читатель WAV на базе `System.IO.Pipelines`: разбирает заголовок, читает сырые или нормализованные double-сэмплы по каналам, конвертирует между PCM16/24/32 и IEEE float, выдаёт потоковые чанки с настраиваемым размером батча.

---

### [Sa.Media.FFmpeg](src/Sa.Media.FFmpeg) — Обёртка FFmpeg для .NET

Кроссплатформная обёртка над FFmpeg со встроенными статическими бинарниками (win-x64, linux-x64) — работает без установки. Включает извлечение метаданных, конвертацию аудио (PCM S16/S32 LE, F32 LE, raw, MP3, OGG), разделение/объединение каналов, потоковый ввод/вывод через стримы и DI.

---

### [Sa.Utils.WorkQueue](src/Sa.Utils.WorkQueue) — Асинхронная очередь с ограничением параллелизма

Высокопроизводительная асинхронная очередь задач на `System.Threading.Channels` с ограниченной ёмкостью и back-pressure (стратегии заполнения при переполнении: `Wait`/`Skip`/`Throw`). Изменяемый на лету параллелизм (`ConcurrencyLimit`/`MaxConcurrency`) с динамическим масштабированием, порядком отмены ридеров (`Lifo`•`Fifo`•`RoundRobin`•`Random`) и режимами отмены (`Hard`/`Soft`). DI, безопасный (идемпотентный) shutdown, стратегии ошибок и обратные вызовы статусов.

---

## Примеры

В `src/Samples/`:

| Образец | Описание |
|---------|----------|
| [Configuration.Web](src/Samples/Configuration.Web) | CLI-аргументы + секреты в ASP.NET |
| [FFMpeg.Console](src/Samples/FFMpeg.Console) | Извлечение метаданных FFmpeg |
| [HybridFileStorage.Console](src/Samples/HybridFileStorage.Console) | Мульти- провайдерное хранилище |
| [Partitational.ConsoleApp](src/Samples/Partitational.ConsoleApp) | Декларативное партиционирование |
| [PgOutbox.ConsoleApp](src/Samples/PgOutbox.ConsoleApp) | Паттерн Outbox |
| [Schedule.Console](src/Samples/Schedule.Console) | Планировщик задач |
| [Echo](src/Samples/Echo) | Разделение аудиоканалов на базе Sa.Media |
| [WorkQueue.Console](src/Samples/WorkQueue.Console) | Демо Sa.WorkQueue с изменением параллелизма на лету |

---

## Тесты

В `src/Tests/`: 15 тестовых проектов на **xunit v3** с **Testcontainers** (PostgreSQL + Minio) для интеграционных тестов.

---

## Сборка

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

## Архитектура

- Целевая платформа — **.NET 10.0** с **Native AOT**
- **Central Package Management** — версии в `Directory.Packages.props`
- Общие утилиты в **Sa** линкуются в consuming-проект через `<Compile Include="..." Link="..."/>`
- Все пакеты используют SDK-style csproj с implicit usings, nullable и анализаторами
- Решение управляется через `.slnx`
- Тесты на **xunit v3** с **Testcontainers** (PostgreSQL + Minio), изоляция на класс теста

## Лицензия

MIT
