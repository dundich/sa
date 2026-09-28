# Sa.Utils.WorkQueue — аудит и улучшения

**Дата аудита:** 2026-09-28 · .NET SDK 10.0.112 · пакет версии `0.12.0` (версия не менялась)
**Объём:** `SaWorkQueue.cs` (1094 строки), `ISaWorkQueue.cs`, `SaWorkQueueOptions.cs`, `ThrowHelper.cs`,
`Setup.cs`, `SaWorkQueueFullException.cs`, `SaWorkQueueLogMessages.cs`, оба readme
**Статус:** критичный баг **C1 исправлен и покрыт тестами**; M1–M4, L1–L8, D2–D3 — открыты.
Предыдущие волны (B1–B8, D1–D7) закрыты коммитом `bdb6a15` и в этот документ не входят.

---

## Объём аудита

| Файл | Роль |
|---|---|
| `src/Sa.Utils.WorkQueue/SaWorkQueue.cs` | ридеры, канал, дренаж, dispose/shutdown |
| `src/Sa.Utils.WorkQueue/ISaWorkQueue.cs` | публичный контракт |
| `src/Sa.Utils.WorkQueue/SaWorkQueueOptions.cs` | record-опции + `With*`-билдеры |
| `src/Sa.Utils.WorkQueue/ThrowHelper.cs` | генерация исключений |
| `src/Sa.Utils.WorkQueue/Setup.cs` | DI-регистрация, `CreateSimple` |
| `src/Sa.Utils.WorkQueue/SaWorkQueueFullException.cs` | исключение переполнения |
| `src/Sa.Utils.WorkQueue/{SaEnqueueStrategy,SaExecutionErrorStrategy,SaReaderCancelMode,SaReaderCancellationOrder,SaWorkStatus,SaWorkDrainReason}.cs` | enum-ы и их XML-доки |

Смежный код, повлиявший на выводы:

- `src/Sa.Schedule/Engine/JobScheduler.cs` — единственный консьюмер в репозитории
  (`AbortJob` + `Start`, `Stop` упирается в семантику `WaitForIdleAsync`)
- `src/Tests/Sa.Utils.WorkQueue.Tests/*` — 12 файлов, покрытие до C1 было широким, но сценария
  «ридер завершился синхронно до регистрации» не касался ни один тест
- `src/Common.Properties.xml` — `Nullable=enable`, `EnableNETAnalyzers`, `PublishAot`

Метод: каждая находка воспроизведена на отдельном прогоне против скомпилированной копии
исходников. Сборка и все 108 тестов зелёные, повторный прогон тестов — 5/5 без флака.

---

## Как проверять

```bash
# сборка (из корня)
dotnet build src/Sa.slnx -c Release

# быстрый цикл по одному пакету (из src/ — иначе dotnet test уходит в VSTest и падает)
cd src && dotnet test --project Tests/Sa.Utils.WorkQueue.Tests -c Release

# регрессия потребителя
cd src && dotnet test --project Tests/Sa.ScheduleTests -c Release
```

Docker для этих двух наборов не нужен — Testcontainers используют только PostgreSQL/S3-наборы.

---

## Находки

### Критичные

| # | Находка | Статус |
|---|---|---|
| **C1** | `StartReaderUnderLock` регистрирует ридер **после** запуска петли: ридер, завершившийся синхронно, ломает учёт пула | ✅ **исправлено** |

### Средние

| # | Находка | Симптом |
|---|---|---|
| **M1** | Статус-колбэк выполняется под `_readersSync` | Дедлок, если колбэк ждёт поток, которому нужен `_readersSync` |
| **M2** | `Enqueue(Wait)` на паузе с полным буфером | Продюсер паркуется, пока очередь на паузе; освобождают shutdown, снятие паузы и токен вызывающего |
| **M3** | `ForceCancelReaders*` без `try/finally` | `TimeoutException`/`AggregateException` пропускают дренаж: `IsIdle()` навсегда `false` |
| **M4** | `Cancelled`/`Aborted` уходят в колбэк с `error: null` | Потерян диагностический `OperationCanceledException` |

### Низкие

| # | Находка |
|---|---|
| **L1** | `ConcurrencyLimit = -1` молча ставит паузу навсегда, тогда как конструктор отрицательный лимит отвергает |
| **L2** | Сеттер меняет `_concurrency`/`_paused` вне `if (IsEnabled)` — после shutdown можно «выставить» лимит, которого не будет |
| **L3** | `ThrowHelper.QueueStopped()` выбрасывает исходное исключение, хотя в `WriteAsyncBalanced` оно в области видимости |
| **L4** | `HandleShutdownOnError` перезаписывает `_shutdownError` безусловно — корневая причина теряется |
| **L5** | `_taskCount` объявлен `volatile` **и** мутируется под `lock (_wiSync)` — инвариант размыт |
| **L6** | Пролог «disposed / stopped» из 4 строк продублирован в 4 методах |
| **L7** | `Setup.AddSaWorkQueue(configureOptions, lifetime)` принимает любой `ServiceLifetime`, хотя readme запрещает Scoped/Transient |
| **L8** | `MaxConcurrency < 1` молча заменяется на `ProcessorCount`, тогда как остальные опции валидируются в конструкторе |

### Документация

| # | Находка | Статус |
|---|---|---|
| **D1** | Комментарий «Called without lock» в `OnStatusChanged` был ложным | ✅ **исправлено** |
| **D2** | Readme правило 1 говорит «декремент внутри `RemoveReader`», правило 5 — «декремент eagerly в сеттере». Прямое противоречие двух «никогда»-правил | открыто |
| **D3** | Параметр `failIfPaused` покрывает ещё и «нет живых ридеров» (`QueueHasNoReaders`) — имя врёт | открыто |

---

## Что сделано

### C1 — ридер, завершившийся синхронно, ломал учёт пула

`StartReaderUnderLock` вызывал `ReaderLoopAsync(cts, ctsWork)` **до** добавления CTS в
`_ctsReaders` / `_ctsWorks` / `_taskReaders`. Если буфер уже содержит элемент, а процессор
завершается или падает **без `await`**, тело петли выполняется инлайн, доходит до `finally`
и вызывает `RemoveReader(cts)`, где `_ctsReaders.IndexOf(cts) == -1`.

`System.Threading.Lock` **ре-ентрантен**, поэтому вложенный `RemoveReader` не блокировался, а
молча отработал не по той ветке: счёл неучтённую потерю и списал слот параллелизма.

Воспроизведение до фикса (очередь на паузе, 20 элементов в буфере, синхронно падающий
процессор, `StopReader`, запрошено 4):

```
paused: limit=0 readers=0 pending=8
round 1: requested 4 -> limit=0 readers=4 pending=4 idle=False
round 2: requested 4 -> limit=4 readers=4 pending=4 idle=False   <- 4 "живых", 0 реальных
round 3..6: то же
WaitForIdleAsync: HUNG (токен 2s истёк), IsIdle=False, pending=4
failIfPaused:true  ->  НЕ бросает (эвристика считает, что ридеры есть)
ConcurrencyLimit=0 ->  ObjectDisposedException: The CancellationTokenSource has been disposed.
```

Пять последствий, все подтверждены отдельно:

1. **`RemoveReader` списывал слот у неучтённого ридера** — лимит, только что выставленный
   вызывающим, молча съедался (просили 4 → отдавало 0).
2. **Уже диспоузнутый `cts` дописывался в списки навсегда.** `IsCancellationRequested == false`,
   то есть он выглядит живым; `HasLiveReaders()` врал; список рос на одну запись за re-arm.
3. **`CancelReadersUnderLock` вызывал `cts.Cancel()` без `try`/`catch`** →
   `ObjectDisposedException` вылетал из **публичного сеттера `ConcurrencyLimit`**.
4. **`WaitForIdleAsync` висел вечно.** Проверка «нет живых ридеров» опиралась на
   `HasLiveReaders()`, а мёртвые записи делали её ложной. `failIfPaused: true` не помогал —
   по той же причине.
5. **`ctsWork` не диспоузнился** (в ветке `idx < 0` переменная `work` оставалась `null`),
   регистрация на `_shutdownCts` утекала.

Триггер — не экзотика. Достаточно совпадения трёх обычных вещей: пауза с непустым буфером,
процессор без `await` до `throw` (или `Task.CompletedTask`), и `StopReader` либо
`ShutdownQueue` — а последний является **значением по умолчанию**, то есть
срабатывает вообще без настройки. Дефолтный вариант воспроизведён отдельно.

Прежний аудит записал это как P3 «теоретически». Оно не теоретическое.

#### Фикс

Наблюдение, на котором держится решение: `StartReaderUnderLock` удерживает `_readersSync`
всю регистрацию, поэтому `RemoveReader` **с другого потока** не может увидеть незарегистрированный
ридер — он блокируется на локе. Проблемен только инлайновый путь на том же потоке.

`StartReaderUnderLock`:

```csharp
var task = ReaderLoopAsync(cts, ctsWork);

// A reader can run its WHOLE body synchronously: ... so the loop reaches its
// `finally` and calls RemoveReader before the registration below. Nothing blocks
// it — System.Threading.Lock is re-entrant, so the nested RemoveReader simply
// proceeds and finds this reader unregistered.
var task = ReaderLoopAsync(cts, ctsWork);
if (task.IsCompleted)
{
    return; // removed itself before it was ever tracked; both CTSs are already disposed
}

_ctsReaders.Add(cts);
_ctsWorks.Add(ctsWork);
_taskReaders.Add(task);
```

`task.IsCompleted` — точный сигнал: задача не может завершиться до выполнения своего `finally`,
а завершение на **другом** потоке в это состояние не попадает (там `RemoveReader` блокируется
на локе, который ещё удерживается здесь).

`RemoveReader` — сигнатура расширена до `(cts, ctsWork)`, а ветка `idx < 0` больше не трогает учёт:

```csharp
registered = idx >= 0;
...
intentional = registered && _intentionalRemovals.Remove(cts);
tracked = intentional || (registered && _forceCancelled.Remove(cts));
...
if (registered && !intentional && !tracked && _concurrency > 0)
{
    _concurrency--;
}
...
if (registered && IsEnabled && !intentional && !tracked) { LogReaderLost(...); }
```

Передача `ctsWork` параметром нужна, чтобы диспоузнуть work-CTS в этой ветке — иначе он
не виден нигде и утекает.

Семантика `_concurrency` после фикса: значение отражает **запрошенный** лимит, а «сколько
ридеров реально работает» честно отдаёт `HasLiveReaders()`. Синхронно умерший ридер не был
учтён, поэтому и не списывает слот. Это согласуется с контрактом `StopReader`
(«ёмкость уменьшается, восстановление — выставить `ConcurrencyLimit` снова») и с тем же
аргументом, который уже лёг в фикс B2: умирающий ридер не должен съедать лимит,
выставленный вызывающим.

Проверка после фикса (тот же сценарий):

```
paused: limit=0 readers=0 pending=8
round 1: requested 4 -> limit=4 readers=0 pending=4   # все 4 умерли, учёт чист
round 2: requested 4 -> limit=4 readers=0 pending=0
round 3: requested 4 -> limit=4 readers=4 pending=0   # буфер пуст, ридеры живые
WaitForIdleAsync: вернулся за 1 ms, IsIdle=True
failIfPaused:true не бросает (прогресс возможен)
```

### D1 — ложный комментарий в `OnStatusChanged`

Комментарий утверждал «Called without lock». Теперь он уточняет, что `_wiSync` действительно
не удерживается, но **`_readersSync` — удерживается**: когда ридер стартует при непустом буфере,
элемент подхватывается и отчитывается синхронно внутри `StartReaderUnderLock`. Добавлено
требование: колбэк должен быть самодостаточным (лог и возврат).

Дополнительно у полей локов зафиксировано, что `System.Threading.Lock` ре-ентрантен и что
именно это делает инлайновый `RemoveReader` молча успешным — и почему регистрация не должна
записывать уже завершившуюся задачу.

---

## Что осталось открытым

### M1 — статус-колбэк под `_readersSync` (дедлок)

Воспроизведено: колбэк отдаёт работу в другой поток, которому нужен `_readersSync`, и ждёт
его. Поток, выставивший `ConcurrencyLimit`, держит лок → взаимоблокировка.

Сейчас это задокументировано в коде (см. D1), но **не исправлено**: фикс требует вынести запуск
петли за пределы `_readersSync` (собрать «отложенные старты» и запустить их после выхода из
лока), а это перестройка структуры локов — отдельное изменение со своим ревью.

### M2 — `Enqueue(Wait)` на паузе с полным буфером

Воспроизведено: буфер полон, очередь на паузе, `Enqueue` паркуется бесконечно.
`SaEnqueueStrategy.Wait` — значение по умолчанию, и продюсер `Sa.Schedule` идёт этим путём.

**Уточнение к первоначальной формулировке.** Ранний вариант находки утверждал, что
единственный выход — токен вызывающего. Это не так: парящий продюсер освобождается ещё
двумя способами, и это меняет предлагаемый фикс.

```
buffer full (AvailableCapacity=0), paused
  after 500 ms: parked=True
  Shutdown() after 300 ms  -> enqueue InvalidOp: Queue has been stopped.  (812 ms)
  ConcurrencyLimit = 1     -> enqueue returned true
  only caller token        -> OCE (caller token)
```

- `Shutdown` / `ShutdownAsync` освобождают продюсера: `Writer.TryComplete()` поднимает
  парящий `WriteAsync` через `ChannelClosedException`, который `WriteAsyncBalanced`
  переводит в `ThrowHelper.QueueStopped()`;
- повышение `ConcurrencyLimit` тоже освобождает — ридер забирает элемент, место появляется.

Значит связывать ожидание с `_shutdownCts` **не нужно**: это уже работает, и такой фикс
был бы лишним дублированием. Остаётся задокументировать в `ISaWorkQueue.Enqueue` и в
readme, что на паузе с полным буфером `Wait` блокируется до снятия паузы, shutdown'а или
отмены токена вызывающего.

### M3 — `ForceCancelReaders*` без `try/finally`

`ForceCancelReaders` (`Task.WaitAll(tasks, _shutdownTimeout)`) и `ForceCancelReadersAsync`
(`Task.WhenAll(...).WaitAsync(t, ct)`) не защищены `try`/`finally`, в отличие от `Shutdown`,
где дренаж стоит в `finally`. `TimeoutException` или `AggregateException` пропускают
`DrainAndResetIdle`: буфер и `_taskCount` не сбрасываются.

Воспроизведено (истёкший timeout): после `TimeoutException` `IsIdle() == false` остаётся
навсегда, при этом `WaitForIdleAsync` возвращает «OK» через ветку «нет живых ридеров» — два
API противоречат друг другу.

Нужно решить, что считать правильным: дренаж в `finally` (счётчик согласован, но отброшенное
не сможет забрать выживший ридер) или текущее поведение (буфер цел, но счётчик завис).
В любом случае расхождение `IsIdle()` и `WaitForIdleAsync` стоит устранить — например,
`WaitForIdleAsync` при возврате без простоя логирует предупреждение.

### M4 — `Cancelled`/`Aborted` без исключения

`ExecuteItemAsync` ловит `OperationCanceledException ex`, но вызывает
`OnStatusChanged(item, SaWorkStatus.Cancelled)` / `(…, Aborted)` **без** `ex` —
в отличие от `Faulted`, который исключение всегда несёт. Пойманное исключение выбрасывается.
`ThrowHelper.CallerCancelledException()` и `QueueShutdownException()` для этого уже есть и
используются в дренаже.

### L1 — `ConcurrencyLimit = -1` молча останавливает очередь

Сеттер клампит в `0` (пауза), конструктор отрицательные значения отвергает. Опечатка глушит
очередь навсегда, при этом `IsEnabled` остаётся `true` — диагностики нет. Вариант: `throw`
на отрицательное значение в сеттере (как в конструкторе) либо как минимум лог.

### L2 — сеттер меняет состояние после shutdown

`_concurrency` и `_paused` присваиваются до `if (IsEnabled)`, поэтому после остановки можно
«выставить» лимит, которого никогда не будет. Если это намеренно («запомнить настройку»),
стоит задокументировать; если нет — перенести внутрь `if`.

### L3 — теряется исходное исключение

`ThrowHelper.QueueStopped()` создаёт новый `InvalidOperationException`, хотя в
`WriteAsyncBalanced` причина (`ex`) в области видимости. Стоит передавать её вторым аргументом.

### L4 — `_shutdownError` перезаписывается

`HandleShutdownOnError` присваивает `_shutdownError` безусловно, до попытки `ShutdownAsync`
(которая может вернуться из-за CAS). Поздняя ошибка вытесняет корневую. Стоит писать
только когда поле ещё `null` — тем более, что сам `ShutdownAsync` при повторе
ничего не делает.

### L5 — `volatile` + `lock` на одном поле

`_taskCount` объявлен `volatile` и при этом мутируется под `lock (_wiSync)`, а читается без
лока в `QueueTasks` / `IsIdle()`. Либо оставить `volatile` и убрать лок (тогда нужен
`Interlocked`), либо убрать `volatile` и читать через `Volatile.Read`. Сейчас инвариант
описан в двух местах сразу, что и привело к C1 в соседнем коде.

### L6 — дублирование пролога

`ObjectDisposedException.ThrowIf(...)` + `if (!IsEnabled) ThrowHelper.QueueStopped();`
повторяется в `Enqueue`, `EnqueueMany`, `TryEnqueue`, `WaitForIdleAsync`. Вынести в
`ThrowIfNotActive()`.

### L7 — `ServiceLifetime` в DI

`Setup.AddSaWorkQueue(configureOptions, lifetime)` позволяет `Scoped`/`Transient`, при которых
на каждое разрешение создаётся новая очередь с собственными ридерами; readme (note 1)
прямо запрещает это. Второй перегрузки (`<TProcessor, TInput>`) жёстко шардится Singleton.
Стоит убрать параметр или отклонять не-Singleton.

### L8 — `MaxConcurrency` не валидируется

`options.MaxConcurrency > 0 ? … : Environment.ProcessorCount` — любое значение `< 1` молча
заменяется. `ConcurrencyLimit`, `QueueCapacity` и `ShutdownTimeout` в конструкторе
отвергаются. Стоит добавить `MaxConcurrency` в тот же список проверок.

### D2 / D3 — расхождения в readme и именах

Readme правило 1 («декремент внутри `RemoveReader`») противоречит правилу 5 («декремент
eagerly в сеттере и в `CancelAndTrackReaders`»). Оба — запреты, и правило 1 стоит
переписать под фактическое поведение.

`failIfPaused` покрывает и «нет живых ридеров» (`QueueHasNoReaders`) — имя вводит в
заблуждение. Варианты: переименовать в `failIfNoProgress`, либо добавить второй параметр
`failIfNoReaders` (дефолт `false` сохранит поведение).

---

## Тесты

Новый файл `src/Tests/Sa.Utils.WorkQueue.Tests/WorkQueueSyncReaderTests.cs` — 9 тестов
на C1. Сценарий строится так, чтобы ридер гарантированно умирал синхронно: пауза,
непустой буфер, процессор без `await` до `throw`.

| Тест | Что закрывает |
|---|---|
| `ReArm_WithStopReader_KeepsTheRequestedLimit` | последствие 1: лимит 4 не съедается |
| `ReArm_WithDefaultShutdownQueueStrategy_KeepsTheRequestedLimit` | то же для пути по умолчанию |
| `ReArm_LeavesNoDeadReaderBehind` | последствие 2 + 5: список пуст, ни одного диспоузнутого CTS |
| `LimitDecrease_AfterSynchronousFault_DoesNotThrowObjectDisposedException` | последствие 3: `ObjectDisposedException` из сеттера |
| `WaitForIdle_AfterSynchronousFaultPool_ReapsInsteadOfHanging` | последствие 4: ожидание возвращается, а не висит |
| `WaitForIdle_FailIfPaused_ThrowsWhilePoolEmpty_AndRecoversAfterReArm` | `failIfPaused` видит пустой пул и корректно восстанавливается |
| `RepeatedReArm_DoesNotAccumulateDeadReaders` | последствие 2: 5 раундов, ни одной накопленной записи |
| `ReArm_WithSuspendingProcessor_StillRegistersTheReader` | «здоровая» ветка: ридер, который уступает поток, регистрируется как обычно |
| `ScaleUpAndDown_WithLiveReaders_IsUnaffected` | регрессия: обычное масштабирование 2→5→1→4 не сломано |

Вспомогательные хелперы в файле: `ReaderCts` (рефлексия в `_ctsReaders`),
`WaitForReaderCountAsync` (поллинг до нужного числа — снятие гонки с reaping'ом
отменённых ридеров), `AssertNoDisposedReaders` (`cts.Token` бросает `ObjectDisposedException`
на диспоузнутом источнике).

Покрытия до фикса не было ни у одного из этих сценариев: `StopReader_ReducesConcurrency_NoAutoReplace`
проверяет незапланированный декремент из живого пула, `MultipleStopReaders_CorrectCapacityDecrease` —
конкурентные `StopReader`, а re-arm после синхронного падения не проверял никто.

Тесты к остальным находкам (M2, M3, M4) **не написаны** — они требуют сначала решить,
какое поведение считать правильным (см. M3). После решения они добавляются как
фиксирующие текущую семантику. Для M2 поведение уже установлено (см. раздел выше) —
достаточно теста на две точки выхода, отличные от токена вызывающего, чтобы зафиксировать
текущую семантику без риска для совместимости.

Итог: **108/108** в `Sa.Utils.WorkQueue.Tests` (было 99), **133/133** в `Sa.ScheduleTests`.
Пять повторных прогонов без флака. `dotnet build src/Sa.slnx -c Release` — 0 warnings, 0 errors.

---

## Замечания по .NET 10

- **`System.Threading.Lock` ре-ентрантен.** Это не деталь реализации: именно поэтому
  инлайновый `RemoveReader` в C1 **молча успевал** вместо того, чтобы упасть, и именно
  поэтому дедлок M1 выглядит загадочно. Отмечено комментарием у полей локов.
- **Таймауты не дружат с виртуальным временем.** `Task.WaitAll(tasks, ts)`,
  `Task.WaitAsync(ts)`, `CancelAsync()` — пути с 30-секундным ожиданием нельзя проверить
  без реального сна. Инъекция `TimeProvider` сделала бы M3 тестируемым детерминированно.
- **AOT / тримминг:** замечаний нет. `IsAotCompatible` соблюдён, `[LoggerMessage]`
  генерируетсяsource-gen'ом, `Channel`/`Lock`/`Interlocked` AOT-безопасны.
- `Environment.ProcessorCount` читается один раз в конструкторе: если контейнер получает
  CPU-лимит уже после старта, максимум не подстроится. Приемлемо, но стоит знать.

---

## Вне объёма (отдельный тикет)

- **P1** `CancelAndTrackReaders` — O(n²) (`_ctsReaders.Contains` в цикле) + два LINQ-прохода
- **P2** три параллельных списка `_ctsReaders`/`_ctsWorks`/`_taskReaders` — инвариант, который
  сам readme (правило 6) просит не нарушать. Замена на один `List<Reader>(Loop, Work, Task)`.
  C1 — прямое следствие этого инварианта: «зарегистрировать после старта» пришлось делать
  осторожно, иначе `RemoveReader` удалил бы чужую задачу по индексу
- **P3** закрыт — стал C1
- **P4** `SaWorkQueue.cs` — 1094 строки; разбить на `partial`
- **P5** нет наблюдаемости пула (`int LiveReaders`); `ConcurrencyLimit` после force-cancel
  читается как `0`, а пул пуст не из-за паузы, и снаружи это не различить
