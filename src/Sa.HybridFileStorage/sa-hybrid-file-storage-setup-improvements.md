# Sa.HybridFileStorage* — план улучшений `Setup.cs` (валидация, DI-регистрация, доки)

**Дата аудита:** 2026-09-27 · .NET SDK 10.0 · пакеты версии `0.12.0` → `0.13.0`
**Объём:** 4 файла `Setup.cs` + смежный код, который они регистрируют
**Статус:** все 6 волн выполнены и проверены.
Сборка: 0 warnings / 0 errors. Полный прогон: **1249 тестов, 0 упавших, 5 пропущено**
(19 сборок, `dotnet test -c Release --max-parallel-test-modules 2 --solution Sa.slnx`).
Подробности реализации — в разделе [Что уже сделано](#что-уже-сделано).

## Объём аудита

Прочитаны и проверены:

| Файл | Роль |
|---|---|
| `src/Sa.HybridFileStorage/Setup.cs` | `AddSaHybridFileStorage`, `AddSaInMemoryFileStorage` ×2 |
| `src/Sa.HybridFileStorage.FileSystem/Setup.cs` | `AddSaFileSystemFileStorage` ×2 |
| `src/Sa.HybridFileStorage.Postgres/Setup.cs` | `AddSaPostgreSqlFileStorage` ×3, `RegistrationMarker` |
| `src/Sa.HybridFileStorage.S3/Setup.cs` | `AddSaS3FileStorage`, `RegistrationMarker`, `Defaults` |

Смежный код, повлиявший на выводы:

- `src/Sa.HybridFileStorage/HybridStorageBuilder.cs`, `HybridFileStorageContainer.cs`,
  `Interceptors/Setup.cs`, `FileIdParser.cs`, `InMemoryFileStorageOptions.cs`
- `src/Sa.HybridFileStorage.Postgres/PostgresFileStorage.cs`, `PostgresFileStorageOptions.cs`
- `src/Sa.HybridFileStorage.FileSystem/FileSystemStorageSettings.cs`, `FileSystemStorageOptions.cs`
- `src/Sa.HybridFileStorage.S3/S3FileStorageOptions.cs`
- `src/Sa.Partitional.PostgreSql/Setup.cs`, `Configuration/PartConfiguration.cs`,
  `Configuration/Builder/Setup.cs` (важно: реальное имя папки — **Partitional**, с «t»,
  а namespace — `Sa.Partional.PostgreSql`)
- `src/Sa.Data.S3/Setup.cs`, `S3BucketSettings.cs`, `S3BucketClientSetupSettings.cs`
- `src/Sa.Data.PostgreSql/Configuration/IPgDataSourceSettingsBuilder.cs`, `PgDataSourceSettings.cs`
- README/Readme-ru всех четырёх пакетов + корневой `README.md`
- `src/Tests/Sa.HybridFileStorage{,.FileSystem,.Postgres,.S3}Tests` — фикстуры и тесты

**Проверено:** все три provider-пакета (`FileSystem`, `Postgres`, `S3`) имеют
`ProjectReference` на базовый `Sa.HybridFileStorage` — значит общий public-хелпер
в базовом пакете доступен всем без `InternalsVisibleTo`.

**Проверено:** ни один C#-call site в репозитории не использует удалённую перегрузку
`AddSaFileSystemFileStorage(Action<IServiceProvider, FileSystemStorageOptions>)` —
только два README. Удаление не потребовало правок в тестах или сэмплах.

---

## Как проверять

```bash
# сборка (из корня)
./build-sh/do-build.sh                  # или: dotnet build src/Sa.slnx -c Release

# все тесты (из src/, иначе dotnet test уходит в VSTest-драйвер и падает)
cd src && dotnet test --max-parallel-test-modules 2 --solution Sa.slnx --no-build --no-restore

# быстрый цикл по одному пакету (нужен Docker: Testcontainers PostgreSQL + Minio)
dotnet run --project src/Tests/Sa.HybridFileStorage.PostgresTests
dotnet run --project src/Tests/Sa.HybridFileStorage.S3Tests
dotnet run --project src/Tests/Sa.HybridFileStorage.FileSystemTests
dotnet run --project src/Tests/Sa.HybridFileStorageTests
```

Docker должен быть запущен: каждый integration-класс поднимает свой контейнер.

---

## Сводка находок

| # | Severity | Место | Проблема | Волна | Статус |
|---|----------|-------|----------|-------|--------|
| 1 | 🔴 Blocker | `.Postgres/Setup.cs:118` + `PostgresFileStorage.cs:49` | DDL регистрирует таблицу под **сырым** `TableName`, SQL провайдера использует **`Sanitize()`**. `TableName = "my files"` → DDL создаёт `my files`, запрос идёт в `my_files` → `relation does not exist` | 1, 2 | ✅ |
| 2 | 🔴 Blocker | `.Postgres/Setup.cs:121` + `PostgresFileStorage.cs:142` | `size INT NOT NULL` + `(int)ms.Length` (unchecked) → файл >2 ГБ молча пишет мусорный `size`. Нужен `BIGINT` + `(long)` | 2 | ✅ |
| 3 | 🔴 Blocker | `.Postgres/Setup.cs` | `PostgresFileStorageOptions` **не валидируется вообще**. `StorageType = "s3!"` → property возвращает `"s3!"`, `_schemePrefix` строится из `Sanitize()` = `"s3_"` → провайдер **не может обработать собственный file ID**. `TableName = ""` → битый SQL. `ExpireDays = -1` → `TimeSpan.FromDays(-1)` | 1, 2 | ✅ |
| 4 | 🔴 Blocker | `.Postgres/Setup.cs:107-113` | Авто-определение схемы **мутирует общий `options`** из ленивого `ISettingsBuilder`-factory, а `PostgresFileStorage` читает `SchemaName` в **инициализаторе поля**. Работает только при accidental ordering (`IPartitionManager` резолвится раньше конструкции storage по порядку аргументов фабрики). Любой рефакторинг `PartitionManager` → тихая порча | 2 | ✅ |
| 5 | 🟠 High | `.S3/Setup.cs:40-56` | Guard сравнивает **только** настройки клиента. `AddSaS3FileStorage(o with Basket="a")` + `AddSaS3FileStorage(o with Basket="b")` → второй вызов **молча no-op**, получаете basket `"a"` | 3 | ✅ |
| 6 | 🟠 High | `.FileSystem/Setup.cs:45` | `new ServiceCollection().BuildServiceProvider()` — фантомный провайдер: колбэк не может resolve `IConfiguration`/`IHostEnvironment`/пользовательские сервисы. XML-дока это признаёт. Плюс ручная копия 4 из 5 полей теряет `BufferSize` | 1, 4 | ✅ |
| 7 | 🟠 High | `.Postgres/README.md:70-108, 229-274` | README описывает **несуществующий API**: `PostgresFileStorageConfiguration`, `IPostgresFileStorageConfiguration`, `.WithSchemaName/.WithTableName/.WithStorageType`, `PartOptions`/`CleanupOptions` | 6 | ✅ |
| 8 | 🟡 Medium | все 4 файла | `sp.GetService<TimeProvider>() ?? TimeProvider.System` продублирован 6 раз; молчаливый fallback маскирует отсутствие регистрации | 3, 4, 5 | ✅ |
| 9 | 🟡 Medium | все 4 файла | Нет `ArgumentNullException.ThrowIfNull(services)` | 1, 2, 3, 4, 5 | ✅ |
| 10 | 🟡 Medium | `.S3/Setup.cs:26-29, 95-101` | `ArgumentNullException.ThrowIfNullOrWhiteSpace` → неверный тип исключения для whitespace. `Defaults` — слишком generic имя в публичном namespace. `options.Region ?? Defaults.DefaultRegion` — мёртвый код | 1, 3 | ✅ |
| 11 | 🟡 Medium | `.S3/Setup.cs:31-38, 60` | `TryAddSingleton(settings)` регистрирует `S3BucketClientSetupSettings`, `AddSaS3BucketClient` — `S3BucketSettings`. Дублирование. Плюс `S3FileStorageOptions` не даёт настроить `TotalRequestTimeout`/`HandlerLifetime`/`ConnectionPoolLifetime` | 3 | ✅ |
| 12 | 🟡 Medium | `Sa.HybridFileStorage/Setup.cs:17-25` | Второй вызов `AddSaHybridFileStorage` **молча теряет** конфиг: `TryAddSingleton` — no-op, а `_configureStorages`/`_configureInterceptors` второго builder'а не выполняются | 5 | ✅ |
| 13 | 🟡 Medium | `Sa.HybridFileStorage/Setup.cs:59-63` | Hybrid-overload создаёт storage через `new` мимо DI → не виден в `sp.GetServices<IFileStorage>()`, не участвует в disposal | 5 | ✅ |
| 14 | 🟢 Low | `.FileSystem` | Два почти идентичных `Validate()`; в `FileSystemStorageOptions.Validate():59` `catch (Exception ex)` перехватывает **свой собственный** `ValidationException` и переупаковывает. `Basket.Length` → NRE при `null` | 1 | ✅ |
| 15 | 🟢 Low | `.Postgres/Setup.cs:163`, `.S3/Setup.cs:84` | Два `internal sealed class RegistrationMarker` с одинаковым именем в разных сборках; `internal`-тип зарегистрирован как `ServiceType` | 2, 3 | ✅ |
| 16 | 🟢 Low | `.Postgres/Setup.cs:117-127` | DDL-литералы инлайн; `SchemaName` интерполируется в SQL без санитизации | 2 | ✅ |
| 17 | 🟢 Low | `Sa.HybridFileStorage/HybridStorageBuilder.cs:47-69` | Ни одного storage → `IHybridFileStorage` резолвится с пустым контейнером, `CanProcess` всегда `false`. Тихая поломка | 5 | ✅ |
| 18 | 🟢 Low | `.S3/Sa.HybridFileStorage.S3.csproj` | Не было `InternalsVisibleTo` для тестового проекта — маркер регистрации нельзя было проверить | 3 | ✅ |

---

## Что уже сделано (волны 1–4)

### Волна 1 — фундамент валидации ✅

**`src/Sa.HybridFileStorage/StorageNaming.cs`** (новый, public static class в базовом пакете) —
единый источник правил именования: `DefaultBasket`, `BasketMinLength=3`, `BasketMaxLength=63`,
`StorageTypeMaxLength=10`, `IdentifierMaxLength=63`, и методы `ValidateBasket`,
`RequireStorageType`, `RequireIdentifier`.

Правила выведены из реальных ограничений потребителей, а не скопированы:

- `Basket` — 3..63, первый символ буква/`_`, **без разделителей пути**. Дефисы разрешены:
  корзина используется дословно как имя каталога в FS/in-memory провайдерах и как сегмент
  пути в file ID. Строгий «только `[A-Za-z0-9_]`» сломал бы существующий тест
  `FailoverTests.cs` с корзиной `"basket-a"`.
- `StorageType` — до 10 символов, **без `:` `/` `\`**, потому что `FileIdParser` находит
  разделитель схемы через `IndexOf("://")`. Дефисы и точки разрешены (`azure-blob` валиден).
- `RequireIdentifier` — «голый» SQL-идентификатор `[A-Za-z_][A-Za-z0-9_]*`. Кавычки
  (`"files"`) отвергаются, а не обрезаются.

**FileSystem:** `FileSystemStorageSettings` → `sealed record`, валидация через `StorageNaming`,
добавлен `BufferSize`. `FileSystemStorageOptions` → `sealed record` + `BufferSize` +
`ToSettings()` (единственный маппинг между типами) + `Validate()`, делегирующий
`ToSettings().Validate()`. Исправлен `catch` (`when (ex is not ValidationException)`) и NRE
на `Basket.Length`.

**Postgres:** `PostgresFileStorageOptions.Validate()` — `TableName`/`SchemaName` через
`RequireIdentifier`, `StorageType` через `RequireStorageType`, `Basket` через
`ValidateBasket`, плюс `ExpireDays >= 1` и `MigrationScheduleForwardDays >= 1`. Вызывается
из `RegisterCore` **до** любой регистрации, так что невалидные опции оставляют
service collection пустым.

**S3:** `S3FileStorageOptions` → `sealed record`, добавлены `Validate(string paramName)` и
`ToBucketClientSettings()`. `ToBucketClientSettings` стартует с настоящего
`new S3BucketClientSetupSettings`, а не с продублированных констант — иначе дефолты
молча разъезжаются с `Sa.Data.S3`.

**Тесты:** `StorageNamingTests` (43), `FileSystemStorageOptionsTests` (15),
`PostgresFileStorageOptionsTests` (33), `S3FileStorageOptionsTests` (23).

### Волна 2 — корректность Postgres ✅

- `PostgresFileStorageSchema` (новый internal-синглтон) резолвит схему **один раз**:
  явное имя → первый элемент `search_path` из connection string → `"public"`.
  Мутация общего `options` из ленивого `ISettingsBuilder`-factory и зависимость от
  accidental ordering устранены. Резолв обёрнут в `try/catch` — data source, который не может
  сообщить `search_path`, не должен ронять регистрацию.
- `PostgresFileStorage` больше не вызывает `Sanitize()`: опции валидируются на регистрации,
  поэтому `TableName`/`Basket`/`StorageType`/`SchemaName` используются дословно. Мёртвый
  `Sanitize` удалён. Это закрывает сразу три расхождения (находки 1, 3 и `Basket`-divergence).
- `PostgresFileStorageTable` — DDL вынесен в отдельный internal-класс,
  `size` → `BIGINT`, `created_at` не объявляется (его добавляет `PartByRange` как
  `bigint NOT NULL`). `NpgsqlParameter<int>("size", (int)ms.Length)` →
  `NpgsqlParameter<long>("size", ms.Length)`.
- `RegistrationMarker` → `PostgresFileStorageRegistration`; перегрузка
  `AddSaPostgreSqlFileStorage(PostgresFileStorageOptions)` теперь **копирует** опции
  (вызывающая сторона больше не может влиять на зарегистрированный storage).
- `SchemaName` валидируется как одиночный идентификатор (запятая в списке `search_path`
  отвергается).
- `AddSaPartitional` уже регистрирует `TimeProvider` — используется `GetRequiredService`.

**Тесты:** `PostgresFileStorageOptionsTests` (33), `PostgresFileStorageRegistrationTests` (19 —
валидация на регистрации, идемпотентность, резолвер схемы), `PostgresFileStorageCustomNamesTests`
(4 — round-trip с нестандартными `TableName`/`StorageType`/`Basket` и проверка, что `size`
действительно `bigint`). Итого 59 в PostgresTests, все зелёные.

### Волна 3 — S3 ✅

- `S3FileStorageRegistration` хранит и `S3FileStorageOptions`, и настройки клиента.
  Сравнение клиентского таргета — только `Endpoint`/`Bucket`/`Region`; расхождение
  `Basket`/`StorageType`/`IsReadOnly` → `InvalidOperationException` с внятным сообщением.
- Сравнение опций — `S3FileStorageOptions.HasSameStorageIdentity(...)`, а **не** равенство
  record'а. Это всплыло уже на тестах: `record`-равенство включает `AccessKey`/`SecretKey`/
  `ClientSettings`, из-за чего обычная ротация креденшелов давала ложное «другой basket».
  Клиент всё равно first-wins и не пересобирается, поэтому дрейф креденшелов и транспортных
  настроек — no-op, а не конфликт.
- `Validate()` даёт правильный тип исключения по BCL-конвенции:
  `ArgumentNullException` для `null`, `ArgumentException` для whitespace
  (было наоборот — «Value cannot be null» на пустой строке). Плюс проверка, что `Endpoint`
  — абсолютный http(s)-URL, и `StorageNaming` для `StorageType`/`Basket`.
- `Defaults` → `S3Defaults`; мёртвый `options.Region ?? Defaults.DefaultRegion` убран.
- `ClientSettings` проброшен в `ToBucketClientSettings()`; дублирующий
  `TryAddSingleton(settings)` удалён. Время ожидания/пул/handler настраиваются.
- `S3FileStorage` больше не содержит недостижимого fallback `Basket → "share"`.
- В `.S3.csproj` добавлен `InternalsVisibleTo` для `Sa.HybridFileStorage.S3Tests`.

**Тесты:** `S3FileStorageOptionsTests` (23), `S3FileStorageRegistrationTests` (19). Всего 38 в
S3Tests, все зелёные (включая интеграционные с Minio).

### Волна 4 — FileSystem API ✅

- Перегрузка `AddSaFileSystemFileStorage(Action<IServiceProvider, FileSystemStorageOptions>)`
  **удалена** (без `[Obsolete]`-шиммера), вместо неё —
  `AddSaFileSystemFileStorage(Action<FileSystemStorageOptions>)`. Фантомный
  `new ServiceCollection().BuildServiceProvider()` исчез.
- Новый overload сводится к `options.Validate()` + делегированию в
  `AddSaFileSystemFileStorage(services, options.ToSettings())` — ручная копия полей,
  терявшая `BufferSize`, больше не существует ни в одной точке.
- `AddSaFileSystemFileStorage(FileSystemStorageSettings)` делает `options with { }`:
  у вызывающей стороны больше нет изменяемой ссылки на зарегистрированный storage.
- `TryAddSingleton(TimeProvider.System)` + `GetRequiredService<TimeProvider>()`.
- `README.md:110` и `Readme-ru.md:110` обновлены в обоих языках.

**Тесты:** `FileSystemStorageOptionsTests` (15), `FileSystemStorageRegistrationTests` (13).
Всего 43 в FileSystemTests, все зелёные.

---

## Волна 5 — ядро Hybrid ✅

**`src/Sa.HybridFileStorage/Setup.cs`** и **`HybridStorageBuilder.cs`** переписаны.

- **Защита от двойного вызова (#12).** `AddSaHybridFileStorage` теперь проверяет
  `services.Any(d => d.ServiceType == typeof(IHybridFileStorage))` и бросает
  `InvalidOperationException`. Проверка идёт по сервис-типу, а не по внутреннему builder'у,
  поэтому регистрация, сделанная руками, тоже обнаруживается. Сообщение объясняет, что
  именно потерялось бы, и предлагает регистрировать провайдеры их собственными `Add...`.
- **Провайдер создаётся контейнером (#13).** `HybridStorageBuilder` получил внутренний
  `ConfigureServices(Action<IServiceCollection>)`, применяемый в `Build()` **до** добавления
  фабрики `IHybridFileStorage`. Hybrid-overload `AddSaInMemoryFileStorage` регистрирует
  `IFileStorage` через DI, поэтому инстанс виден в `sp.GetServices<IFileStorage>()`,
  участвует в disposal и — по ссылке — тот же самый, что попадает в гибридный контейнер.
  Отложенный колбэк добавляет резолвнутый DI-инстанс; `HybridFileStorageContainer`
  дедуплицирует по ссылке, так что двойного добавления нет.
  Fallback на случай внешней реализации `IHybridFileStorageConfiguration` сохранён
  (`GetRequiredService<IFileStorage>()`) — публичный интерфейс не менялся.
- **Пустой контейнер → throw (#17).** Фабрика `IHybridFileStorage` бросает
  `InvalidOperationException`, если ни один storage не попал в контейнер. Раньше резолв
  проходил, и каждая операция падала с `HybridFileStorageNoAvailableException` — сообщение
  указывало на место вызова, а не на отсутствие регистрации.
- **`TimeProvider` и null-проверки (#8, #9).** `TryAddSingleton(TimeProvider.System)` +
  `GetRequiredService<TimeProvider>()`; `ArgumentNullException.ThrowIfNull(services)` первым
  statement во всех точках входа.

**Два существующих теста фиксировали старое поведение** и обновлены под новое — обе
семантики документированы в разделе «Ломающие изменения»:

| Тест | Было | Стало |
|---|---|---|
| `HybridFileStorageTests.WhenStorageIsEmptyThrowsInvalidOperationException` | `HybridFileStorageNoAvailableException` на `UploadAsync` | `InvalidOperationException` на резолве. Имя теста уже заявляло `InvalidOperationException` — расходилось только тело |
| `FailoverTests.HybridFileStorage_NoAvailableStorage_ThrowsNoAvailableException` | то же | `InvalidOperationException` на резолве, переименован в `…_ThrowsOnResolve` |

**Тесты:** новый `HybridRegistrationTests` — 14 тестов (двойная регистрация, пустой
контейнер, инстанс из DI, `TimeProvider`). Всего в `Sa.HybridFileStorageTests` **101**,
все зелёные.

Два замечания по ходу:

- `InMemoryFileStorageOptions.Basket` **не валидируется** через `StorageNaming`:
  пустая корзина — поддерживаемая конфигурация (file ID становится двухсегментным,
  `scheme://tenant/file`), и на неё завязано несколько существующих тестов.
  `MaxSizeBytes` тоже не валидируется: «ноль или меньше отключает лимит» задокументировано.
- `IHybridFileStorage` не имеет `CanProcess` — проверки в тестах идут по
  `hybrid.Storages`, а `IFileStorage.CanProcess` у in-memory сравнивает только
  `StorageType` (`"mem"`), **не** корзину. Первые версии тестов проверяли basket-специфичные
  file ID и падали.

---

## Волна 6 — документация и версии ✅

- **`Sa.HybridFileStorage.Postgres/README.md`** переписан: Quick Start, схема определения,
  таблица валидации, `PostgresFileStorageOptions` (плоские свойства вместо
  `StorageOptions`/`PartOptions`/`CleanupOptions`), таблица методов регистрации, DI-сервисы.
- **`Readme-ru.md`** (Postgres) синхронизирован: добавлены «Как определяется схема»,
  «Валидация опций на этапе регистрации», таблица методов регистрации, DI-сервисы,
  модель данных с `BIGINT` и «Ломающие изменения».
- **`WithSearchPath` убран** — метода нет в `IPgDataSourceSettingsBuilder` (там только
  `WithConnectionString`). В обоих языках описано, что `search_path` берётся из строки
  подключения и парсится без обращения к БД. Единственное оставшееся упоминание —
  фраза в русском README «отдельного метода `WithSearchPath` у билдера нет».
- **DDL в обоих README** приведён к фактическому: `size BIGINT`, `created_at BIGINT`,
  `name VARCHAR(512)`, `file_ext VARCHAR(64)`, `basket VARCHAR(63)`.
- **«Ломающие изменения»** добавлены во все четыре пакета на обоих языках, включая
  обязательный скрипт миграции:

  ```sql
  ALTER TABLE <schema>.<table> ALTER COLUMN size TYPE BIGINT;
  ```

- **S3 README**: добавлены `ClientSettings`, раздел валидации, явно расписанная
  идемпотентность (в русском файле старая формулировка «одинаковый таргет включает
  credentials» была неточной — креденшелы конфликтом не считаются).
- **FileSystem README**: убрана ссылка на удалённую
  `Action<IServiceProvider, FileSystemStorageOptions>`, `BufferSize` добавлен в таблицу
  опций, расписана fail-fast валидация, приведён миграционный пример.
- **Корневой `README.md`**: пример hybrid-регистрации не компилировался
  (`ConfigureStorage(sp => …)`, несуществующие `S3Storage` и
  `new FileSystemStorage("/data/uploads")`) — переписан на реальный API.
- **Версии `0.12.0 → 0.13.0`** во всех четырёх `.csproj`.

Финальная проверка после всех правок:

```
dotnet build src/Sa.slnx -c Release        →  0 Warning(s), 0 Error(s)
dotnet test -c Release --no-build --no-restore \
    --max-parallel-test-modules 2 --solution Sa.slnx
                                           →  total 1249, failed 0, succeeded 1244, skipped 5
```

Первый полный прогон прошёл по `bin/Debug` (старые бинари) и дал 1169 тестов — это был
артефакт запуска без `-c Release`, а не разница в покрытии; прогон по свежей Release-сборке
приведён выше.

---

## Порядок и зависимости

Волны строго последовательные: волна 2 опирается на `StorageNaming` из волны 1,
волна 3 — на `S3FileStorageOptions` как record из волны 1, волна 5 — на `TimeProvider`-паттерн,
введённый в волнах 3–4. Каждая волна собирается и тестируется отдельно.

| Волна | Содержание | Breaking | Статус |
|---|---|---|---|
| 1 | `StorageNaming` + `Validate()` во всех опциях + `BufferSize` | нет | ✅ |
| 2 | Postgres: имена, `BIGINT`, резолвер схемы, `SchemaName` | да (DDL) | ✅ |
| 3 | S3: guard, валидация, `S3Defaults`, `ClientSettings` | да (переименование) | ✅ |
| 4 | FileSystem: новая перегрузка, удаление старой | да (удаление перегрузки) | ✅ |
| 5 | Hybrid: маркер, DI-путь, пустой контейнер, `TimeProvider` | да (throw на резолве) | ✅ |
| 6 | Доки, «Breaking changes», версии `0.13.0` | — | ✅ |

---

## Заметки по ходу реализации

1. **Строгий charset для `Basket` был бы регрессией.** Первая версия `ValidateBasket`
   требовала `[A-Za-z0-9_]`, но корзина используется дословно как имя каталога и как
   сегмент пути — дефис валиден, и `FailoverTests.cs` его использует. Ограничение сузили до
   «3..63, первая буква/`_`, без разделителей пути».
2. **`record`-равенство ≠ равенство хранилища.** Сравнение `S3FileStorageOptions` целиком
   оказалось неверным: оно включает креденшелы, которые всё равно нельзя применить. Отсюда
   `HasSameStorageIdentity` (находка #18 в таблице выше — это не отдельный дефект, а следствие).
3. **Двойная-обёртка `ValidationException` трудно воспроизвести на Linux.**
   `Path.GetFullPath` отвергает `\0` раньше, чем сработает `IndexOfAny(GetInvalidPathChars())`,
   так что тест на отсутствие вложенности написан как инвариант («префикс встречается не
   более одного раза»), а не как проверка конкретной ветки.
4. **Ленивое создание таблицы в Postgres.** Корневая партиционированная таблица появляется
   только при первой загрузке, поэтому проверка типа колонки `size` через
   `information_schema` должна идти **после** upload в том же тесте.
5. **Резолвер схемы тестируется без БД.** `GetSearchPath()` парсит connection string,
   поэтому весь набор кейсов (`"storage,public"`, `" storage , audit"`, `","`, отсутствие
   `IPgDataSource`) проверяется на обычном `ServiceCollection` без контейнера.

---

## Что осознанно НЕ входит в план

- `PostgresFileStorage.cs` в целом (CRUD, `FileIdParser` как таковой) —
  только те места, на которые прямо указывают находки (1, 2, 3, 4).
- `AddSaS3BucketClient` в `Sa.Data.S3` — не-идемпотентность отмечалась как
  «проверить/закрыть» в волне 3. Проверено: после удаления дублирующего
  `TryAddSingleton(settings)` повторный вызов `AddSaS3FileStorage` ничего не добавляет,
  потому что guard отсекает его раньше. Правка самого `Sa.Data.S3` вынесена бы в отдельный PR.
- Кэширование схемы/настроек, `IOptionsMonitor`-переподписка — не рассматривалось,
  текущая семантика (singleton, резолв при первом использовании) соответствует остальному репо.
- `data BYTEA NOT NULL` запрещает пустое тело файла — оставлено как есть, чтобы не менять
  контракт хранилища в этой серии.
- `TreatWarningsAsErrors` — в `Common.Properties.xml` включён `EnableNETAnalyzers`,
  но не `TreatWarningsAsErrors`. Опционально: включить для четырёх пакетов,
  чтобы новые диагностики не проскальзывали.
