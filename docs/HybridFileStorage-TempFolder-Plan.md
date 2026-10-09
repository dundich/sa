# Sa.HybridFileStorage.FileSystem — план работ: файловые операции на Sa.Data.TempFolder

Решения приняты в ревью (2026-10-08). Пакеты 0.x — breaking changes разрешены.

Итог:

1. Файловые операции (`Upload/Download/Delete`) провайдера переводятся на
   `Sa.Data.TempFolder` — сырой `FileStream`/`File.Delete` из `FileSystemStorage`
   уходит.
2. `Setup` провайдера переделывается на keyed с настройками TempFolder
   (регистрация **только с именем**, обязательный канал `.TempFolder(...)`).
3. Доделывается delete: новый `ITempFolder.DeleteAsync` + перенаправление на него
   `FileSystemStorage.DeleteAsync`.

Базовый проект `src/Sa.HybridFileStorage` не трогаем: его
`IHybridFileStorage.DeleteAsync` уже реализован, оркестрация потоков остаётся как есть.
`IFileStorage` остаётся additive в цепочке гибрида (порядок = failover).

## Принятые решения

| # | Вопрос | Решение |
|---|--------|---------|
| 1 | Область | только `src/Sa.HybridFileStorage.FileSystem` + `src/Sa.Data.TempFolder` |
| 2 | Delete | добавить `ITempFolder.DeleteAsync`, `FileSystemStorage.DeleteAsync` делегирует ему |
| 3 | Корень | `FileSystemStorageOptions.BasePath` **удалить**; корень = `TempFolderOptions.RootPath` через канал `.TempFolder(...)` |
| 4 | TTL | для storage-регистраций **свой дефолт: `MaxAge = 30 дней`**, если не задан ни в конфигурации, ни пользователем |
| 5 | Канал `.TempFolder(...)` | **обязательный**: без него — `InvalidOperationException` на регистрации |
| 6 | `BufferSize` | **убрать** из `FileSystemStorageOptions` (I/O владеет TempFolder, буфер фиксированный 81920) |
| 7 | Форма Setup | **только с именем**: `AddSaFileSystemFileStorage(string name, ...)` (breaking, как named-only в S3) |
| 8 | Delete: директория | `InvalidOperationException` (контракт един с `DownloadAsync`) |
| 9 | Прекаллокация | **перенести** эвристику FS-провайдера в `TempFolder.SaveStreamAsync` |
| 10 | Коммиты | **два**, каждый собирается и зелёный сам по себе |

---

## Коммит 1 — `feat(Sa.Data.TempFolder)`: `DeleteAsync` + прекаллокация

### 1. `ITempFolder.DeleteAsync(string path, CancellationToken ct = default)` → `Task<bool>`

Контракт (прототип — `DownloadAsync` из прошлого раунда):

- путь abs/rel → `PathGuard`: escape/инъекция → `SecurityException`;
  blank → `ArgumentException`; **директория → `InvalidOperationException`**;
- мутация → read-only экземпляр: `InvalidOperationException`;
- **lease в `CleanupGate` на всю операцию** (идущий проход чистки отменяется,
  новый не стартует; чистка при своей работе отступает перед удалением);
- ретраи `Retry.Linear` (3 попытки, transient IO — как у cleanup); исчерпаны →
  `IOException` наружу (решает вызывающий — у провайдера это `catch → false`);
- файла нет / гонка «унесли между проверкой и вызовом» → `false`, без исключения;
- валидация и проверка ct — **до** входа в гейт (отклонённый путь не отменяет
  идущий проход).

Файлы: `src/Sa.Data.TempFolder/ITempFolder.cs`, `TempFolder.cs`.

### 2. Прекаллокация в `SaveStreamAsync`

Перенос эвристики `FileSystemStorage.UploadAsync`: если входящий поток
`CanSeek && Length > 0 && Length ≤ int.MaxValue` → `PreallocationSize = (int)Length`
в `FileStreamOptions`. Поведение загрузки не деградирует, benefit для всех
потребителей TempFolder.

### 3. Тесты (`src/Tests/Sa.Data.TempFolderTests`, плоские — ловушка дочерних namespace)

~10 штук: формы пути (abs/rel), инъекции/escape, blank, read-only, отсутствие →
`false`, директория → `InvalidOperationException`, две защиты гейта
(удаление посреди прохода отменяет его; чистка отступает во время delete),
поведение прекаллокации (контент пишется корректно).

### 4. Readme EN/RU (`src/Sa.Data.TempFolder/Readme.md`, `Readme-ru.md`)

Строка API `DeleteAsync`, буллет «cleanup уступает чтению, записи и удалению»,
Cleanup §4, таблица ошибок, read-only секция (delete отклоняется), упоминание
прекаллокации в строке `SaveStreamAsync`.

---

## Коммит 2 — `feat(Sa.HybridFileStorage.FileSystem)`: на TempFolder + keyed Setup

### 1. csproj

`ProjectReference` → `Sa.Data.TempFolder` (паттерн S3 → `Sa.Data.S3`;
CPM: без `<Version/>`).

### 2. Опции

Убрать `BasePath` и `BufferSize` (свойство, валидатор, доки, тесты).
Остаются `StorageType` / `Basket` / `IsReadOnly`.
`FileSystemStorageOptionsValidator` — переписать под оставшиеся.

### 3. `FileSystemStorage(ITempFolder, FileSystemStorageOptions, TimeProvider)`

Схема fileId `fs://` и layout `{Basket}/{tenant}/{file}` **не меняются**:

- `_root = tempFolder.RootPath`; containment/`CanProcess` — по нему
  (локальный helper вместо `IsPathWithinBase`);
- `UploadAsync` → sanitize + `metadata.Validate()` →
  `tempFolder.SaveStreamAsync(stream, "{Basket}/{tenant}/{file}", ct)` →
  `StorageResult` (`AbsoluteUrl` = абсолютный путь из Save);
- `DownloadAsync` → `tempFolder.DownloadAsync(...)` (false если нет файла —
  контракт уже совпадает);
- `DeleteAsync` → `EnsureWritable` → `tempFolder.DeleteAsync(...)`; локальный
  `catch IOException → false` остаётся у провайдера (поведение не меняется);
- `GetMetadataAsync` — без изменений.

Файлы: `src/Sa.HybridFileStorage.FileSystem/FileSystemStorage.cs`, `.csproj`.

### 4. Setup — только с именем (breaking)

```csharp
services.AddSaFileSystemFileStorage("share", b => b
    .FromConfiguration("FileSystemStorage")
    .TempFolder(tb => tb                        // ОБЯЗАТЕЛЬНЫЙ канал
        .FromConfiguration("TempFolder")
        .Options(ob => ob.Configure(o => o.MaxAge = TimeSpan.FromDays(60)))));
```

- blank имя → `ArgumentException`;
- **дубль имени → `InvalidOperationException`**; разные имена → две независимые
  регистрации (текущий гард «только один провайдер в коллекции» снимается —
  отложенный multi-instance из `7119417`); маркер-регистрация — keyed по имени;
- без `.TempFolder(...)` → `InvalidOperationException` («корень обязателен —
  вызовите `.TempFolder(...)`»);
- внутри: `services.AddSaTempFolder(name, tb)` → keyed `ITempFolder` +
  `TempFolderCleanerHost` (`TryAddEnumerable`);
- **TTL-дефолт 30 дней — трюк с сентинелом** (порядок действий в
  `Sa.Data.TempFolder.Setup`: биндинг секции → наши действия → PostConfigure):
  1. FS записывает **первым** `Configure(o => o.MaxAge = СЕНТИНЕЛ)` —
     выполняется сразу после биндинга секции (значение из секции его перетирает)
     и **до** `Configure` пользователя (пользовательский MaxAge тоже перетирает);
  2. после пользовательских действий — финальный `PostConfigure`:
     `if (MaxAge == СЕНТИНЕЛ) MaxAge = 30 дней`;
  - СЕНТИНЕЛ: `TimeSpan.FromTicks(-1)` (валидация MaxAge выполняется после всех
    PostConfigure — к тому моменту сентинела уже нет);
- `IFileStorage` — фабрика
  `new FileSystemStorage(sp.GetRequiredKeyedService<ITempFolder>(name), options, tp)`;
- `RootPath`: пустой **или равен системному `Path.GetTempPath()`** →
  `OptionsValidationException` с явным сообщением (хранить файлы хранилища
  в системном temp почти наверняка ошибка);
- `ValidateOnStart` для опций обоих типов; `IValidateOptions<>` по дому
  (без `ValidateDataAnnotations` — AOT);
- `TimeProvider` — `TryAddEnumerable`/keyed по образцу `Sa.Data.TempFolder.Setup`.

Файлы: `src/Sa.HybridFileStorage.FileSystem/Setup.cs`,
`IFileSystemStorageBuilder.cs` (+ канал `.TempFolder(...)`),
`FileSystemStorageOptions.cs`.

### 5. Тесты (`src/Tests/Sa.HybridFileStorage.FileSystemTests`)

- существующие фикстуры/CRUD/retry/конкурентность — регистрация по новой схеме;
- новые:
  - keyed: дубль имени → throw; два имени → две независимые регистрации;
    `ITempFolder` резолвится по ключу; нет `.TempFolder()` → throw;
  - нет имени (старая перегрузка) → compile-break, вызовы обновлены;
  - e2e TTL: файл протухает → `CleanupAsync` → `DownloadAsync` = false;
  - upload реально лежит под `RootPath` (не в системном temp);
- `FileRetryBehaviorTests`: ретраи delete теперь на уровне TempFolder (коммит 1) —
  поведенческие кейсы (есть → true, нет → false) остаются здесь, детерминированные
  ретраи переезжают;
- `Sa.HybridFileStorageTests` — проверить вызовы `AddSaFileSystemFileStorage`
  (named-only) и обновить.

### 6. Readme EN/RU (FS) + базовый

`src/Sa.HybridFileStorage.FileSystem/Readme.md` / `Readme-ru.md`:

- именованная регистрация, обязательный `.TempFolder(...)`;
- таблица опций без `BasePath`/`BufferSize` + секция канала TempFolder;
- корень/размещение: layout от `TempFolderOptions.RootPath`;
- TTL-дефолт 30 дней, предупреждение о `TempFolderCleanerHost`;
- миграционная заметка: «задайте `RootPath` = старый `BasePath` — старые
  fileId/файлы остаются валидными»;
- семантика delete (true/false/исключения), security (PathGuard).

`src/Sa.HybridFileStorage/Readme*.md` — проверить примеры с
`AddSaFileSystemFileStorage` и поправить вызовы.

### 7. Проверка

- `dotnet build src/Sa.slnx -c Release` (NU1900-шум ≠ ошибки);
- `Sa.Data.TempFolderTests`, `Sa.HybridFileStorage.FileSystemTests`,
  `Sa.HybridFileStorageTests`, `SaTests`;
- полный прогон по AGENTS: из `src/`, `--max-parallel-test-modules 2`,
  `--no-build --no-restore`, `MSBUILDDISABLENODEREUSE=1`, без `-v`;
  Docker — для Testcontainers-сьютов;
- коммиты по одному после зелёной проверки каждого.

---

## Риски / нюансы

- порядок биндинга секции vs дефолтный MaxAge — решается сентинелом (см. п. 4);
- `FileSystemStorageRegistration` (маркер) становится keyed;
- двойной `Setup`-класс (`Sa.Data.TempFolder.Setup` vs
  `Sa.HybridFileStorage.FileSystem.Setup`) — в тестах FS не тянуть
  `using Sa.Data.TempFolder` без alias;
- старые `fs://` fileId валидны только при `RootPath` = прежнем `BasePath`
  (задокументировать);
- чистка стареет только top-level папки под `FolderPrefix` (default `""`) —
  если prefix непустой, `{Basket}` выпадет из TTL; `SaveStreamAsync`
  debounced-touch держит `{Basket}/{tenant}` свежим.

**Статус: план, не реализован.**
