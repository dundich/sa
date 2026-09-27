# Sa.Utils.WorkQueue — план улучшений (баги, гонки, доки)

**Дата аудита:** 2026-09-27 · .NET SDK 10.0 · пакет версии `0.12.0` (версия не менялась)
**Объём:** `SaWorkQueue.cs` (962 строки), интерфейс, опции, `Setup.cs`, лог-сообщения, оба readme
**Статус:** волны 1–3 выполнены и проверены. Детали — в [Что уже сделано](#что-уже-сделано).

## Объём аудита

| Файл | Роль |
|---|---|
| `src/Sa.Utils.WorkQueue/SaWorkQueue.cs` | ридеры, канал, дренаж, dispose/shutdown |
| `src/Sa.Utils.WorkQueue/ISaWorkQueue.cs` | публичный контракт |
| `src/Sa.Utils.WorkQueue/SaWorkQueueOptions.cs` | record-опции + `With*`-билдеры |
| `src/Sa.Utils.WorkQueue/ThrowHelper.cs` | генерация исключений |
| `src/Sa.Utils.WorkQueue/Setup.cs` | DI-регистрация, `CreateSimple` |
| `src/Sa.Utils.WorkQueue/SaWorkQueueFullException.cs` | исключение переполнения |
| `src/Sa.Utils.WorkQueue/{SaEnqueueStrategy,SaExecutionErrorStrategy,SaReaderCancelMode,SaReaderCancellationOrder,SaWorkStatus}.cs` | enum-ы и их XML-доки |

Смежный код, повлиявший на выводы:

- `src/Sa.Schedule/Engine/JobScheduler.cs` — единственный консьюмер в репозитории
  (`AbortJob` + `Start` воспроизводят гонку B2, `Stop` упирается в B7)
- `src/Tests/Sa.Utils.WorkQueue.Tests/*` — 9 файлов, ~2300 строк, покрытие до B1 широкое,
  но `SaReaderCancellationOrder.Random`/`Fifo` **не тестировались вообще**
- `src/Samples/WorkQueue.Console/Program.cs` — пример использования
- `src/Common.Properties.xml` — общие свойства (`Nullable=enable`, `EnableNETAnalyzers`)

**Проверено:** `ISaWorkQueue<TInput>` реализуется в репозитории только классом
`SaWorkQueue<TInput>` — внешних реализаторов нет, поэтому добавление параметра
с значением по умолчанию в `WaitForIdleAsync` безопасно внутри репо.

---

## Как проверять

```bash
# сборка (из корня)
dotnet build src/Sa.slnx -c Release

# быстрый цикл по одному пакету (из src/ — иначе dotnet test уходит в VSTest и падает)
cd src && dotnet test --project Tests/Sa.Utils.WorkQueue.Tests -c Release --no-build --no-restore -v n

# регрессия потребителя
cd src && dotnet test --project Tests/Sa.ScheduleTests -c Release --no-build --no-restore -v n
```

Docker для этих двух наборов не нужен — Testcontainers используют только PostgreSQL/S3-наборы.

---

## Находки

### Критичные

| # | Находка | Симптом |
|---|---|---|
| **B1** | `RandomIndices` — Фишер–Йетс по массиву длины `toCancel` при индексации до `totalCount - 1` | `IndexOutOfRangeException` наружу из сеттера `ConcurrencyLimit`; очередь остаётся с `_concurrency = 2` и 4 живыми ридерами |
| **B2** | `RemoveReader` декрементит `_concurrency` для уже учтённых force-cancel ридеров | `ConcurrencyLimit` «съедается» умирающими ридерами: запрошено 4 → отдаёт 0 при 4 живых ридерах |

### Средние

| # | Находка | Симптом |
|---|---|---|
| **B3** | `DrainAndResetIdle` общий для shutdown и force-cancel | Текст «Queue was shut down» на живой очереди (статус `Faulted` сохранён сознательно) |
| **B4** | `Shutdown`/`ShutdownAsync`: `TryComplete`+дренаж внутри `try` | Исключение из `CancelAsync` оставляет канал открытым, `_taskCount` вечно > 0, `IsIdle()` навсегда `false`, продюсер в `WriteAsync` висит вечно |
| **B5** | `CancelAndTrackReaders` фильтрует `IsCancellationRequested` вне лока | Двойной инкремент `_pendingRemovals` → постоянный дрейф → неверные решения сеттера |
| **B6** | re-entrant `Shutdown`/`ForceCancelReaders`/`Dispose` из процессора | Блокировка на полный `ShutdownTimeout` (30 с по умолчанию) — ридер ждёт собственную задачу |
| **B7** | `WaitForIdleAsync` определяет «паузу» эвристикой по `_concurrency` | Молча возвращается с 4 необработанными элементами; вместе с B7 радиусом B2 это ломает `JobScheduler.Stop()` |

### Низкие

| # | Находка |
|---|---|
| **B8** | Первичный record-ctor `SaWorkQueueOptions` обходит все guard-ы `With*`: `QueueCapacity: -5` → `ArgumentOutOfRangeException` из глубины `Channel.CreateBounded`; `ConcurrencyLimit: -3` → молчаливая пауза; `ShutdownTimeout` ≤ 0 → `ArgumentOutOfRangeException` из `Task.WaitAll` |
| **D1** | Док `SaEnqueueStrategy.Skip`: «not reported via the status callback» — код репортит `Skipped` |
| **D2** | Док `SaExecutionErrorStrategy.StopReader`: «it will be replaced» — не заменяется, лимит падает + warning `ReaderLost` |
| **D3** | Публичные ctor-ы `SaWorkQueueFullException` оставляют `QueueCapacity`/`QueuedCount` = 0 (читается как «пусто») |
| **D4** | `NoWarn 1701;1702;CS8602` в csproj мёртв: пакет собирается с **0 warnings** и без `CS8602` |
| **D5** | `ThrowHelper.QueueStopped()` без `[DoesNotReturn]` |
| **D6** | `Setup.AddSaWorkQueue<TInput>` использует `Add` вместо `TryAdd` (двойная регистрация → throw из `GetRequiredService`); `CreateSimple` игнорирует `MaxConcurrency` |
| **D7** | `SaWorkQueueOptions.Create(process)` без null-check |

### Вне объёма (P1–P5, отдельный тикет)

- **P1** `CancelAndTrackReaders` — O(n²) (`_ctsReaders.Contains` в цикле) + 2 LINQ-последовательности
- **P2** три параллельных списка `_ctsReaders`/`_ctsWorks`/`_taskReaders` — инвариант, который сам
  Readme (правило 6) просит не нарушать; замена на один `List<Reader>(CtsLoop, CtsWork, Task, Generation)`
- **P3** `ReaderLoopAsync` теоретически может завершиться синхронно до добавления в списки →
  вечная мёртвая запись в `_ctsReaders`
- **P4** `SaWorkQueue.cs` — 962 строки; разбить на `partial`
- **P5** нет наблюдаемости пула (`int LiveReaders`)

---

## Что уже сделано

### B1 — `SaReaderCancellationOrder.Random`

`RandomIndices` выполнял Фишер–Йетс по массиву длины `toCancel`, но индекс `j` достигает
`totalCount - 1`. Любое уменьшение лимита, где `toCancel < live`, падало. Воспроизведено
до фикса:

```
order=Random start ConcurrencyLimit=4
decrease THREW IndexOutOfRangeException: Index was outside the bounds of the array.
  ConcurrencyLimit now = 2
```

Фикс: Фишер–Йетс по буферу размера `totalCount`, наружу отдаются первые `toCancel` элементов.

### B2 — дрейф `ConcurrencyLimit`

`CancelAndTrackReaders` только помечает removal (`_pendingRemovals++`), а декремент
`_concurrency` происходит позже, в `finally` ридера. Окно между этими событиями использовал
`Sa.Schedule`: `AbortJob` запускает `ForceCancelReadersAsync` через `Task.Run`, а `Start`
выставляет `_queue.ConcurrencyLimit = _limit`. Воспроизведено до фикса:

```
PROBE2 drift: requested=4 reported=0 (expected 4)   // при этом живы 4 ридера
```

Следствия: `WaitForIdleAsync` возвращался мгновенно, `JobScheduler.RefreshConcurrency`
останавливал все контроллеры.

Фикс без новых полей: учёт слота перенесён в тот же критический участок, где принимается
решение об отмене, — декремент `_concurrency` делается сразу при пометке force-cancel,
а `RemoveReader` теперь декрементит **только** для неучтённых (`!intentional && !tracked`)
ридеров. Поведение последовательного force-cancel не изменилось — тест
`ForceCancelReaders_ConcurrencyLimitReflectsZero` остаётся зелёным.

### B5 — гонка двойного учёта

Учёт и отмена разнесены по разным критическим участкам; фильтр `!IsCancellationRequested`
брался из снимка вне лока. Переписано так, что весь учёт атомарен и идемпотентен: CTS,
уже учтённый как `_intentionalRemovals`/`_forceCancelled` либо уже отменённый, пропускается;
`cts.Cancel()` вызывается уже после постановки на учёт.

### B4 — незакрывающийся writer

`Writer.TryComplete()` и `DrainAndResetIdle()` перенесены в `finally` — исключение из
`_shutdownCts.CancelAsync()` (например, `ObjectDisposedException` от гонки с `Dispose`)
больше не оставляет очередь в состоянии «`Shutdown`, но канал открыт».

### B7 — флаг паузы вместо эвристики

`WaitForIdleAsync` определял паузу как `_concurrency == 0`, что срабатывало и при аварийной
потере ридеров. Введено явное поле `_paused`, взводимое **только** присваиванием
`ConcurrencyLimit = 0`, и параметр `failIfPaused`:

```csharp
Task WaitForIdleAsync(CancellationToken cancellationToken = default, bool failIfPaused = false);
```

Значение по умолчанию сохраняет прежнее поведение; `failIfPaused: true` бросает
`InvalidOperationException`, если лимит явно обнулён. Проверка `_taskCount == 0` осталась
первой — реальный простой возвращается всегда, даже на паузе.

Вторая ветка того же сорта: после force-cancel или сбоев `StopReader` пул может опустеть
**без** паузы — очередь остаётся Active и принимает новые элементы, но читателей,
способных их обработать, нет. В ожидании такого недостижимого «простого»
`WaitForIdleAsync` не висит вечно: если очередь Active, но живых читателей нет
(`HasLiveReaders()`: живых = `_ctsReaders.Count - _pendingRemovals`), ожидание
возвращается немедленно, а с `failIfPaused: true` бросает `InvalidOperationException`
(текст `ThrowHelper.QueueHasNoReaders` указывает на восстановление `ConcurrencyLimit`).
После повторного выставления лимита ожидание работает как обычно. Очередь, созданная
с `ConcurrencyLimit: 0`, взводит `_paused = true` прямо в конструкторе.

### B3 — текст исключения при force-cancel

Статус `Faulted` для отброшенных элементов сохранён сознательно (поведение потребителей,
считающих `Faulted` как ошибку, не меняется). `DrainAndResetIdle` теперь принимает причину
(`DrainReason.Shutdown` / `DrainReason.ForceCancel`) и подставляет соответствующий текст.

### B6 — документированное ограничение

В `ISaWorkQueue` на `Shutdown`/`ShutdownAsync`/`ForceCancelReaders`/`ForceCancelReadersAsync`
добавлен запрет вызывать их из `ISaWork<TInput>.Execute`: вызов блокирует поток на
`ShutdownTimeout` (по умолчанию 30 с), потому что ридер ждёт собственную задачу.
Проверено до фикса (при `ShutdownTimeout = 2 s`):

```
PROBE5b Shutdown()             blocked  2009 ms inside the processor
PROBE5b ForceCancelReaders()   blocked  2004 ms inside the processor
PROBE5b Dispose()              blocked  2000 ms inside the processor
```

Корректный обход уже показан в `JobScheduler.AbortJob` — `Task.Run` с fire-and-forget.

### B8 и D1–D7

Валидация перенесена в ctor `SaWorkQueue` (единая точка до создания канала),
`[DoesNotReturn]` на `ThrowHelper.QueueStopped()`, `SaWorkQueueOptions.Create` с null-check,
`TryAdd` в `Setup.AddSaWorkQueue<TInput>`, `init`-свойства у `SaWorkQueueFullException`,
поправлены доки `Skip`/`StopReader`, из csproj убран мёртвый `NoWarn ...;CS8602`.

### Тесты

- `WorkQueueCancellationOrderTests.cs` (новый) — все четыре порядка отмены: уменьшение не
  бросает, отменяет ровно `delta`, оставшиеся элементы дорабатываются (×20 повторов).
  До фикса `Random`/`Fifo` не были покрыты ни одним тестом.
- `WorkQueueStabilityTests.cs` — гонка force-cancel с выставлением лимита: умирающие
  ридеры не «съедают» лимит, поставленный после отмены (до фикса: запрошено 4 →
  отчитано 0); `failIfPaused` на явной паузе с ожидающей работой; `WaitForIdleAsync`
  на пустом пуле (после force-cancel, на паузе с момента создания) возвращается
  немедленно вместо зависания, а `failIfPaused: true` превращает это в ошибку.
- `WorkQueueOptionsValidationTests.cs` (новый) — конструктор очереди отклоняет
  `QueueCapacity < 1`, отрицательный `ConcurrencyLimit`, неположительный
  `ShutdownTimeout` (первичный record-ctor обходит guard-ы `With*`); граничные
  `QueueCapacity: 1` / `ConcurrencyLimit: 0` принимаются и очередь работает.
- `WorkQueueDrainTests.cs` — причина дренажа в статусе force-cancel; shutdown завершает
  writer и дренирует даже при исключении из `CancelAsync` (инъекция callback-а в
  `_shutdownCts` через reflection — других способов воспроизвести нет).
