# Sa.Hybrid* — план работ: «папка Basket как набор бэкендов»

Основание: [HybridFileStorage-Basket-Concept.md](./HybridFileStorage-Basket-Concept.md)
(решения приняты). Пакеты 0.x — breaking changes разрешены.

## Этап 0 — Ядро: семантика пробора и порядок (Sa.HybridFileStorage)

**Цель:** папка = упорядоченный список backend'ов; операция идёт по ним, пока не получит
успешный результат.

1. `HybridFileStorage.ExecuteStorageOperationAsync`:
   - **Upload** — без изменений: первый успешный (failover), исключение → следующий.
   - **Download/Delete/GetMetadata** — `false`/«не найдено» от storage **не
     завершает** операцию: продолжаем со следующим storage **того же
     `StorageType`** (решения, п. 1/7). Кандидаты уже фильтруются по схеме
     fileId (`CanProcess`); правило делает тип-ограничение явным: после `false`
     инстанса типа `T` опрашиваются только инстансы типа `T`, другой тип —
     стоп. Исключения → следующий storage **любого** типа (failover как сейчас).
   - Итог: исключения от всех кандидатов → `HybridFileStorageAggregateException`;
     «нигде не нашли» → `false`, без исключения; ни одного кандидата →
     `HybridFileStorageNoAvailableException` (как сейчас).
2. `GetMetadataAsync` — уже пробует по порядку, привести к той же семантике явно.
3. Порядок регистрации — зафиксировать правило и задокументировать в XML-doc:
   - порядок = порядок дескрипторов `IFileStorage` в `IServiceCollection`
     (т.е. порядок вызова `Add*`), хранилища из `ConfigureStorage(...)` идут после;
   - in-memory, добавленный через `IHybridFileStorageConfiguration`, встаёт в позицию
     вызова `AddSaHybridFileStorage` — считать это документированным контрактом или
     выровнять (решить во время этапа, тест на порядок обязателен).
4. Реестр папок (new): поднять до явной сущности то, что уже есть —
   `basket → [storages в порядке регистрации]`. Минимум: внутренняя группировка в
   `HybridFileStorageContainer` (`IReadOnlyList<IFileStorage> GetBasket(string basket)`),
   публичный контракт не ломать без нужды.
5. **Порядок типов (решение 8):**
   - типы цепочки папки = `GetBasket(basket)` → фильтр/сортировка по эффективному
     списку типов: непустой override папки → непустой глобальный → порядок
     регистрации (все типы);
   - пропуск перечисленного, но незарегистрированного типа — молча; неперечисленный
     зарегистрированный тип — исключён из цепочки (включая `EnsureWritable`);
   - внутри типа — порядок регистрации (стабильная сортировка по списку);
   - программный канал этапа 0: API на builder'е гибридного ядра
     (например `OrderTypes(params string[])` глобально + per-basket override);
     configuration-канал — этап 1 (`FromConfiguration`).
6. Тесты: `FailoverTests` (новый кейс: два backend одной папки, первый возвращает
   `false` → читаем из второго), тест порядка, регрессия upload-first-wins;
   `OrderingTests` (новые): список `[fs, mem]` меняет порядок цепочки, молчаливый
   пропуск отсутствующего типа, исключение неперечисленного типа (upload идёт в
   перечисленный, в не перечисленный — нет → `NoAvailable`/`Writable` по семантике),
   override папки поверх глобального, пустая настройка = порядок регистрации,
   два одинаковых типа — порядок регистрации внутри типа.

**Файлы:** `HybridFileStorage.cs`, `HybridFileStorageContainer.cs`, `Exceptions.cs`,
`Tests/Sa.HybridFileStorageTests/*`.

**Статус: ✅ выполнен** (см. также §«Решения» концепции, пп. 1/7/8):

- `ExecuteStorageOperationAsync` — общий helper с предикатом `isNotFound` и
  `onUnavailable`: после «не найдено» — тип-стоп (пин по `StorageType`, break на
  смене типа), определённый miss побеждает собранные по пути исключения;
  исключения не ставят пин (failover через любой тип); «нигде не нашли» →
  `false`/`null` без исключения; все исключения → `AggregateException`;
  0 кандидатов → `NoAvailableException` (lookup → `null`). Upload — без изменений.
- `GetMetadataAsync` переписан на тот же helper: кандидаты = `CanProcess(fileId)`
  (раньше опрашивались все подряд), ошибки → failover/aggregate.
- Реестр папок: `HybridFileStorageContainer.GetBasket(basket)` (регистрация +
  фильтр/сортировка по порядку типов) и `OrderCandidates(basket?, candidates)` для
  read-путей (fileId → папка через `FileIdParser.TryParse`, без парса — глобальный
  список); `HybridFileStorage` теперь зависит от concrete-контейнера (internal,
  публичный контракт не тронут).
- Порядок типов: новый `StorageTypeOrdering` (нормализация: пусто = не задано,
  дубликаты в списке схлопываются, внутри типа — порядок регистрации), builder-API
  `OrderTypes(params string[])` + `OrderBasketTypes(basket, params string[])` на
  `IHybridFileStorageConfiguration`; configuration-канал — этап 1, п. 5.
- Порядок регистрации зафиксирован в XML-doc (`AddSaHybridFileStorage`,
  `IHybridFileStorage.Storages`): in-memory из pipeline встаёт в позицию вызова
  `AddSaHybridFileStorage` — задокументировано как контракт (не выровнено).
- Тесты: `FailoverTests` +6 (продолжение пробора после miss в download/delete/
  metadata, тип-стоп, upload-first-wins, exception → failover через тип),
  `OrderingTests` (новый, 12 кейсов) + `FakeFileStorage`; порядок регистрации —
  3 кейса в `OrderingTests`. Прогон: `Sa.HybridFileStorageTests` 120 passed,
  `FileSystemTests` 55 passed; PG/S3-сьюты — в этом окружении нет Docker
  (интеграция WSL), прогнать на машине с Docker (этап 6).

## Этап 1 — Options: секция на папку + готовые экземпляры

**Цель:** у каждого провайдера — единый конфигурационный канал.

1. **InMemory** (ядро): `AddSaInMemoryFileStorage` — overload'ы с
   `FromConfiguration(section)` и `IOptions<InMemoryFileStorageOptions>` /
   `Action<IOptionsBuilder<…>>`.
2. **Postgres**: перенос на options-pipeline — `AddSaPostgreSqlFileStorage` c
   builder'ом в стиле `IFileSystemStorageBuilder` (`FromConfiguration` + `Options`),
   `ValidateOnStart()` вместо эger-`Validate()` при регистрации. Fail-fast переносится
   на старт хоста — отразить в readme провайдера.
3. Именование: опции каждой папки — отдельный named options instance
   (`services.AddOptions<T>(basketName)`), имя = имя папки. Внутренне провайдер
   резолвит `IOptionsMonitor<T>`/`IOptionsSnapshot` по своему имени папки
   (или готовый экземпляр, если передан напрямую — приоритет: явный экземпляр →
   секция → дефолт).
4. Валидаторы (`IValidateOptions`) — через `TryAddEnumerable`, уже так; убедиться,
   что работают для named instances.
5. **Порядок типов (решение 8), configuration-канал:** `FromConfiguration(section)`
   для глобального списка типов контейнера и для per-basket override (секция
   папки); приоритет и правила — как в этапе 0 (непустой override → непустой
   глобальный → порядок регистрации; отсутствующий тип — молча, неперечисленный —
   исключён).

**Риск:** перенос PG на options-pipeline меняет момент fail-fast —
`ValidateOnStart` требует host; в тестах без host валидация срабатывает при первом
чтении `.Value` (поведение уже задокументировано для fs/s3 — повторить).

**Файлы:** `Sa.HybridFileStorage/Setup.cs`, `Sa.HybridFileStorage.Postgres/Setup.cs`,
`PostgresFileStorageOptions.cs`, builders.

## Этап 2 — FileSystem: несколько инстансов

**Цель:** два fs с разными `BasePath` (и/или разными папками) в одной коллекции.

1. Снять guard `FileSystemStorageRegistration` (или переопределить: разрешать N
   регистраций, конфликт = одинаковое имя папки + одинаковый путь).
2. Каждый вызов `AddSaFileSystemFileStorage(...)` — свой named options instance
   (имя = Basket из конфигурации; при прямом `Basket`-аргументе — имя инстанса).
3. Одинаковый `IValidateOptions` — enumerable, уже ок.
4. Тесты: два fs → одна папка (пробор), две папки (изоляция), escape-проверка
   `EnsurePathWithinBase` для каждого BasePath.

**Файлы:** `Sa.HybridFileStorage.FileSystem/Setup.cs`, `FileSystemStorageOptions*`,
`FileSystemStorage.cs` (мало: он уже инстанс-ориентирован).

## Этап 3 — PostgreSQL: несколько папок, партиции одной таблицы

**Цель:** N pg-папок; папка = ветка `LIST (basket)` **одной** таблицы
(решение 7 концепции; две pg в одной папке — не проектируются).

1. Снять guard `PostgresFileStorageRegistration` («different options throws»):
   разрешать N регистраций с разным `Basket`; конфликт = та же папка второй раз.
2. **Одна таблица — один DDL-граф** (проверено в коде): `SchemaBuilder.AddTable`
   возвращает существующий `TableBuilder` и **дописывает** поля/партиции
   повторно (`AddFields` → `AddRange`, `PartByList` → `AddRange`), а `SqlBuilder`
   строит `Dictionary` по `FullName` — повторный `AddTable` той же таблицы из
   второго вызова даст дубли колонок в DDL. Значит: делегат `AddSaPartitional`,
   регистрирующий таблицу, выполняется **один раз на таблицу** (guarded по
   `(schema, tableName)` в pg-провайдере), каждый вызов добавляет только
   `IFileStorage` со своим `Basket`.
3. `PostgresFileStorageSchema` — singleton, схема одна на БД — **оставить**,
   документировать.
4. Расписания: `AddPartMigrationSchedule`/`AddPartCleanupSchedule` — один раз на
   таблицу; `ExpireDays`/`ForwardDays` общие на таблицу (last-wins) —
   задокументировать (решение 7: retention один на таблицу). Spike подтверждён:
   `AddSettings`/`AddSchedule` аккумулируют делегаты (`AddSingleton(configure)`),
   `JobSettings` мержатся по `(JobId, JobType)` — дубль-джобов не будет.
5. `IPartitionManager` один на таблицу — ок: `EnsureParts(table, day, [tenantId, basket])`
   создаёт ветку папки на лету.
6. Тесты: две папки pg на одну таблицу (DDL зарегистрирован один раз, ветки обеих
   папок создаются), повторная папка — guard, failover pg→fs, изоляция папок
   (fileId папки A не читается папкой B).

**Файлы:** `Sa.HybridFileStorage.Postgres/Setup.cs`, `PostgresFileStorage*`;
возможно идемпотентность `AddTable` в `Sa.Partitional.PostgreSql/Configuration/Builder/SchemaBuilder.cs`
(только если не решится guarded'ом в pg-провайдере).

**Вне скоупа:** `Sa.Data.PostgreSql` (одна БД — data source не трогаем),
второй pg в одной папке (решение 7).

## Этап 4 — S3: несколько инстансов (выход в Sa.Data.S3)

**Статус: ✅ реализовано** (2026-10-07). Итоговый механизм: `AddSaS3BucketClientCore(services, clientName, settingsFactory)` — именованный `HttpClient` + keyed `S3BucketSettings` и keyed `IS3BucketClient` под `clientName`; публичный API — **только именованная регистрация** `AddSaS3BucketClient(name, ...)`: безымянный перегруз `AddSaS3BucketClient()` и unkeyed-псевдонимы (`GetRequiredService<IS3BucketClient>()`) **удалены** (решение владельца, breaking change, 0.x — обратная совместимость не нужна); резолв — keyed `GetRequiredKeyedService<IS3BucketClient>(name)`, дубль имени — guard `InvalidOperationException`. Провайдер — N регистраций, гвард снят. Отклонение от текста ниже: ключ клиента — **имя регистрации** (то же, что имя named options), а не имя папки — две S3-папки могут делить один Basket (failover), ключевать по имени папки нельзя.

**Цель:** два S3-папки (разные бакеты/эндпоинты) в одной коллекции.

1. `Sa.Data.S3.Setup.AddSaS3BucketClientCore` — сейчас typed HttpClient
   (`AddHttpClient<IS3BucketClient, S3BucketClient>(ClientName, …)`) с фиксированным
   именем. Нужно: N клиентов — либо keyed services (`AddKeyedHttpClient`), либо
   фабрика `IS3BucketClient` по имени. Спайк в `Sa.Data.S3` (пакет общий —
   сохранить обратную совместимость `AddSaS3BucketClient()` без параметров).
2. `Sa.HybridFileStorage.S3`: снять guard `S3FileStorageRegistration`, named options,
   каждый инстанс — свой клиент (keyed по имени папки).
3. Тесты: два бакета, одинаковые fileId-схемы, пробор до второго бакета.

**Файлы:** `Sa.Data.S3/Setup.cs`, `Sa.HybridFileStorage.S3/*`.

## Этап 5 — Readme (все, en + ru)

- `Sa.HybridFileStorage/Readme.md` + `Readme-ru.md` — раздел
  «Virtual Folders = набор backend'ов» переписать: комбинации (два fs, fs+pg+s3),
  порядок = порядок регистрации, пробор «пока не нашли» (внутри одного типа),
  upload-first-wins, pg — один инстанс на папку (папка = ветка партиций).
- `FileSystem`, `Postgres`, `S3` readmes — multi-instance примеры + options-канал
  (секция на папку, готовый `IOptions<T>`).
- PG readme — смена момента fail-fast (ValidateOnStart) + «две папки pg на одну
  таблицу, retention общий на таблицу».

## Этап 6 — Сборка и тесты

```
./build-sh/do-build.sh
cd src && dotnet test --max-parallel-test-modules 2 --solution Sa.slnx --no-build --no-restore
```

- Docker обязателен (Testcontainers-сьюты pg/s3).
- Затронутые сьюты точечно:
  `dotnet test Tests/Sa.HybridFileStorageTests` (и FileSystem/Postgres/S3 Tests).
- Breaking changes: обновить `Samples/HybridFileStorage.Console`, `Samples/Storage.Tests`.

## Порядок и зависимости

| # | Этап | Зависит от | Оценка |
|---|------|-----------|--------|
| 0 | Ядро (пробор, порядок, реестр) | — | средняя |
| 1 | Options (все провайдеры) | 0 (имена папок) | средняя |
| 2 | FS multi-instance | 1 | малая |
| 3 | PG multi-basket (партиции одной таблицы) | 1 (спайк done — аккумуляция делегатов работает; **но** `AddTable` недидемпотентен → один DDL-граф на таблицу) | средняя |
| 4 | S3 multi-instance | 1, спайк `Sa.Data.S3` (keyed/named clients) | крупная |
| 5 | Readme | 0–4 (итоговый API) | малая |
| 6 | Финальная сборка + все тесты | 0–5 | малая |

Этапы 2 и 4 независимы после этапа 1; **этап 4 — самый рискованный** (выход в общий
`Sa.Data.S3` с typed HttpClient). Этап 3 сузился: спайк подтвердил аккумуляцию
делегатов, но вскрыл недидемпотентность `SchemaBuilder.AddTable` — DDL-граф на
таблицу регистрируется один раз; вторая pg-папка = новый `IFileStorage` со своим
`Basket`, не новая таблица.
