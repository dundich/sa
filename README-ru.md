# Sa — инфраструктурные библиотеки для .NET 10

**Русский** | [English](README.md)

Готовые строительные блоки для распределённых .NET-сервисов: конфигурация, доступ к данным, transactional outbox, файловое хранилище, медиа, планирование. Совместимы с **Native AOT**, `nullable` включён, без ORM.

> ⚠️ **До 1.0.** Все пакеты пока на `0.x.y`, поэтому публичный API может меняться ломающим образом между релизами без предупреждения.

Название каждой библиотеки — ссылка на её каталог, а в каждом каталоге есть `Readme.md` с API, регистрацией в DI, настройками и примерами. Здесь — только ответ на вопрос «какую задачу решает библиотека».

## Содержание

- [Конфигурация](#конфигурация)
- [Данные](#данные)
- [Outbox](#outbox)
- [Файловое хранилище](#файловое-хранилище)
- [Медиа](#медиа)
- [Планирование и параллелизм](#планирование-и-параллелизм)
- [Примеры](#примеры)
- [Сборка и тесты](#сборка-и-тесты)
- [Подробности](#подробности)
- [Лицензия](#лицензия)

## Конфигурация

- **[Sa.Configuration](src/Libraries/Configuration/Sa.Configuration)** — типизированный доступ к аргументам командной строки и к секретам из файлов, переменных окружения или host-key файлов.
- **[Sa.Configuration.PostgreSql](src/Libraries/Configuration/Sa.Configuration.PostgreSql)** — хранит конфигурацию в таблице PostgreSQL, поэтому изменение применяется на лету, без редеплоя.

## Данные

- **[Sa.Data.PostgreSql](src/Libraries/Data/Sa.Data.PostgreSql)** — тонкая обёртка над Npgsql: именованные источники данных, помощники запросов, типизированный маппинг строк, транзакции; без ORM.
- **[Sa.Data.S3](src/Libraries/Data/Sa.Data.S3)** — S3-совместимый клиент для работы с бакетами и объектами.
- **[Sa.Data.TempFolder](src/Libraries/Data/Sa.Data.TempFolder)** — один управляемый временный каталог на задачу: безопасные подпапки, очистка по возрасту, контроль свободного места.
- **[Sa.Partitional.PostgreSql](src/Libraries/Misc/Sa.Partitional.PostgreSql)** — декларативное партиционирование таблиц: партиции создаются заранее, старые данные удаляются по политике хранения.

## Outbox

Паттерн [transactional outbox](https://microservices.io/patterns/data/transactional-outbox.html): сообщение записывается в той же транзакции, что и бизнес-изменение, а затем доставляется с повторами.

- **[Sa.Outbox](src/Libraries/Outbox/Sa.Outbox)** — ядро, не зависящее от провайдера: контракты, батчи, повторы, статусы доставки. Включает in-memory провайдер для разработки и тестов.
- **[Sa.Outbox.PostgreSql](src/Libraries/Outbox/Sa.Outbox.PostgreSql)** — готовый к продакшену провайдер для PostgreSQL.
- **[Sa.Outbox.Redis](src/Libraries/Outbox/Sa.Outbox.Redis)** — провайдер для Redis: уже пригоден к использованию, но его API ещё может меняться.

## Файловое хранилище

- **[Sa.HybridFileStorage](src/Libraries/Hybrid/Sa.HybridFileStorage)** — единый интерфейс загрузки/скачивания/удаления поверх нескольких бэкендов с автоматическим последовательным фейловером между ними.
  - Провайдеры: **[FileSystem](src/Libraries/Hybrid/Sa.HybridFileStorage.FileSystem)**, **[S3](src/Libraries/Hybrid/Sa.HybridFileStorage.S3)**, **[PostgreSQL](src/Libraries/Hybrid/Sa.HybridFileStorage.Postgres)**.

## Медиа

- **[Sa.Media](src/Libraries/Media/Sa.Media)** — экономный по памяти полностью асинхронный читатель WAV: разбор заголовка, сэмплы по каналам, PCM16/24/32 и IEEE float.
- **[Sa.Media.FFmpeg](src/Libraries/Media/Sa.Media.FFmpeg)** — обёртка над FFmpeg со встроенными статическими бинарниками (win-x64, linux-x64), ставить ничего не нужно: метаданные, конвертация аудио, разделение/объединение каналов, работа через потоки.

## Планирование и параллелизм

- **[Sa.Schedule](src/Libraries/Misc/Sa.Schedule)** — запускает задачи по cron-выражениям, фиксированным интервалам или одноразово, с повторами, лимитами параллелизма и управлением старт/стоп на лету.
- **[Sa.Utils.WorkQueue](src/Libraries/Misc/Sa.Utils.WorkQueue)** — асинхронная очередь задач с ограниченной ёмкостью, back-pressure и лимитом параллелизма, который можно менять на лету.

## Примеры

Десять запускаемых примеров — по одному на библиотеку, с командой запуска и ожидаемым выводом — лежат в [`src/Samples/`](src/Samples/README.md).

## Сборка и тесты

Нужен **.NET 10 SDK**, для интеграционных тестов — запущенный **Docker**.

```bash
./build-sh/do-build.sh     # clean, restore, build
./build-sh/do-test.sh      # собрать и прогнать все тесты
./build-sh/do-package.sh   # собрать NuGet-пакеты
```

Можно и напрямую: `dotnet build src/Sa.slnx -c Release`, затем `dotnet test --solution src/Sa.slnx -c Release --no-build --no-restore`.

Для тех, кто меняет код: тесты поднимают настоящие контейнеры PostgreSQL и SeaweedFS, поэтому правила сборки, фильтрации и ограничения параллелизма описаны в [`AGENTS.md`](AGENTS.md).

## Подробности

Этот README — карта; детали лежат рядом с кодом.

| Что | Где |
|-----|-----|
| API, регистрация в DI, настройки, примеры | `Readme.md` в каталоге каждой библиотеки |
| Синтаксис cron | [English](src/Libraries/Misc/Sa.Schedule/Cron/CRON.md) · [Русский](src/Libraries/Misc/Sa.Schedule/Cron/CRON-ru.md) |
| Общий код `Sa` (линкуется в пакеты, не публикуется) | [src/Libraries/Sa/Readme.md](src/Libraries/Sa/Readme.md) |
| Структура решения, сборка, тесты, упаковка | [AGENTS.md](AGENTS.md) |
| Словарь предметной области | [GLOSSARY.md](GLOSSARY.md) |

## Лицензия

MIT
