# Sa.Utils.WorkQueue — аудит и улучшения

**Дата аудита:** 2026-09-28 · .NET SDK 10.0.112 · пакет версии `0.12.0` (версия не менялась)
**Объём:** `SaWorkQueue.cs` (1094 строки), `ISaWorkQueue.cs`, `SaWorkQueueOptions.cs`, `ThrowHelper.cs`,
`Setup.cs`, `SaWorkQueueFullException.cs`, `SaWorkQueueLogMessages.cs`, оба readme
**Статус:** критичный баг **C1 исправлен и покрыт тестами**; из средних закрыт **M4**,
из низких — **L1, L3, L4, L5, L6, L7, L8**; из документации — **D1, D2, D3**.
Открыты **M1**, **M2** (не дефект поведения, а неточность формулировки), **M3** (требует
продуктового решения) и **L2**.
Предыдущие волны (B1–B8, D1–D7) закрыты коммитом `bdb6a15` и в этот документ не входят.

> **Куда идти дальше:** [`sa-utils-workqueue-refactor-plan.md`](./sa-utils-workqueue-refactor-plan.md).
> Здесь — что найдено и почему; там — что с этим делать, по шагам. Фаза 0 плана (рефакторинг
> `SaWorkQueue.cs`, снятие «правил для AI» из readme) уже выполнена.
>
> **Поправка к C1 после рефакторинга:** причина и симптомы в разделе C1 ниже верны, но
> фикс описан иначе. Сейчас ридер **регистрируется до старта петли**, а не после, поэтому
> «ридера, который умер до регистрации» не существует как отдельной ветки. Побочный эффект:
> при re-arm над непустым буфером с синхронно падающим процессором запрошенный лимит
> **честно уменьшается** (4 запроса → 4 умерли → 0), а не остаётся 4. Обоснование и
> остальные последствия — в плане.

---

## Объём аудита

| Файл | Роль |
|---|---|
| `src/Sa.Utils.WorkQueue/SaWorkQueue.cs` | состояние, канал, диспатч элемента, публичные свойства |
| `src/Sa.Utils.WorkQueue/SaWorkQueue.Readers.cs` | пул: старт, отмена, reaping, ожидание простоя |
| `src/Sa.Utils.WorkQueue/SaWorkQueue.Shutdown.cs` | force-cancel, shutdown, drain, dispose |
| `src/Sa.Utils.WorkQueue/SaWorkQueue.Enqueue.cs` | `Enqueue`/`EnqueueMany`/`TryEnqueue` и отказ при полном буфере |
| `src/Sa.Utils.WorkQueue/ISaWorkQueue.cs` | публичный контракт |
| `src/Sa.Utils.WorkQueue/SaWorkQueueOptions.cs` | record-опции + `With*`-билдеры |
| `src/Sa.Utils.WorkQueue/ThrowHelper.cs` | генерация исключений |
| `src/Sa.Utils.WorkQueue/Setup.cs` | DI-регистрация, `CreateSimple` |
| `src/Sa.Utils.WorkQueue/SaWorkQueueFullException.cs` | исключение переполнения |
| `src/Sa.Utils.WorkQueue/{SaEnqueueStrategy,SaExecutionErrorStrategy,SaReaderCancelMode,SaReaderCancellationOrder,SaWorkStatus,SaWorkDrainReason}.cs` | enum-ы и их XML-доки |

Аудит шёл по монолитному `SaWorkQueue.cs` (1094 строки). Сейчас он разбит на четыре `partial`
по кругу ответственности (фаза 0 плана) — имена в таблице выше отражают уже новое состояние
файла, а не исходное.

Смежный код, повлиявший на выводы:

- `src/Sa.Schedule/Engine/JobScheduler.cs` — единственный консьюмер в репозитории
  (`AbortJob` + `Start`, `Stop` упирается в семантику `WaitForIdleAsync`)
- `src/Tests/Sa.Utils.WorkQueue.Tests/*` — 13 файлов, покрытие до C1 было широким, но сценария
  «ридер завершился синхронно до регистрации» не касался ни один тест
- `src/Common.Properties.xml` — `Nullable=enable`, `EnableNETAnalyzers`, `PublishAot`

Метод: каждая находка воспроизведена на отдельном прогоне против скомпилированной копии
исходников. Сборка и все 117 тестов зелёные, повторный прогон тестов — без флака.

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

| # | Находка | Симптом | Статус |
|---|---|---|---|
| **M1** | Статус-колбэк выполняется под `_readersSync` | Дедлок, если колбэк ждёт поток, которому нужен `_readersSync` | открыто — нужен разбор области видимости блокировки |
| **M2** | `Enqueue(Wait)` на паузе с полным буфером | Продюсер паркуется, пока очередь на паузе | не дефект: освобождают shutdown, снятие паузы и токен вызывающего |
| **M3** | `ForceCancelReadersAsync` без `try/finally` — дренаж пропускался по таймауту **и** по отмене `ct` | `IsIdle()` навсегда `false`, при этом `WaitForIdleAsync` возвращался молча | ✅ закрыто — буфер сохраняется, оба API объявляют состояние |
| **M4** | `Cancelled`/`Aborted` уходят в колбэк с `error: null` | Потерян диагностический `OperationCanceledException` | ✅ **исправлено** |

### Низкие

| # | Находка | Статус |
|---|---|---|
| **L1** | `ConcurrencyLimit = -1` молча ставит паузу навсегда, тогда как конструктор отрицательный лимит отвергает | ✅ **исправлено** — сеттер бросает |
| **L2** | Сеттер меняет `_concurrency`/`_paused` вне `if (IsEnabled)` — после shutdown можно «выставить» лимит, которого не будет | открыто |
| **L3** | `ThrowHelper.QueueStopped()` выбрасывает исходное исключение, хотя в `WriteAsyncBalanced` оно в области видимости | ✅ **исправлено** — `QueueStopped(Exception?)` |
| **L4** | `HandleShutdownOnError` перезаписывает `_shutdownError` безусловно — корневая причина теряется | ✅ **исправлено** — `??=` |
| **L5** | `_taskCount` объявлен `volatile` **и** мутируется под `lock (_wiSync)` — инвариант размыт | ✅ **исправлено** в фазе 0 — под `_pendingSync`, чтение через `Volatile.Read` |
| **L6** | Пролог «disposed / stopped» из 4 строк продублирован в 4 методах | ✅ **исправлено** — общий `ThrowIfNotActive` |
| **L7** | `Setup.AddSaWorkQueue(configureOptions, lifetime)` принимает любой `ServiceLifetime`, хотя readme запрещает Scoped/Transient | ✅ **исправлено** — бросает на регистрации |
| **L8** | `MaxConcurrency < 1` молча заменяется на `ProcessorCount`, тогда как остальные опции валидируются в конструкторе | ✅ **исправлено** — конструктор отвергает отрицательное; `0`/`null` = «процессоры», как задокументировано |

### Документация

| # | Находка | Статус |
|---|---|---|
| **D1** | Комментарий «Called without lock» в `OnStatusChanged` был ложным | ✅ **исправлено** |
| **D2** | Readme правило 1 говорил «декремент внутри `RemoveReader`», правило 5 — «декремент eagerly в сеттере». Прямое противоречие двух «никогда»-правил | ✅ **снято** — раздел «правил для AI» удалён из обоих readme, фазой 0 плана |
| **D3** | Параметр `failIfPaused` покрывал ещё и «нет живых ридеров» (`QueueHasNoReaders`) — имя врало | ✅ **исправлено** — переименован в `failIfNoProgress` |

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

### Волна 2 — M4, L1, L3, L4, L7, L8, D3

Все семь правок объединяет одно: **у ошибки должен быть адресат, и он должен совпадать с тем,
кто спрашивал.** Раньше диагноз терялся в четырёх местах.

**M4 — исключение доходит до статус-колбэка.** В `catch (OperationCanceledException ex)` в
`OnStatusChanged` передавался `null`, хотя `Faulted` всегда несёт исключение. У колбэка нет
своего логгера, поэтому `ex` — единственный источник, по которому подписчик узнаёт, *почему*
элемент остановился. Передаётся именно пойманное `ex`, а не синтетическое
`ThrowHelper.CallerCancelledException()`: в дренаже реального исключения нет, и там синтетика
уместна, а здесь оно есть и оно настоящее. Единственное различие между двумя статусами —
инициатор отмены, и оба несут один и тот же OCE.

**L3 — `QueueStopped` несёт причину.** `WriteAsyncBalanced` ловит исключение, которым канал
сообщил о завершении writer'а, и выбрасывал вместо него голое
`InvalidOperationException("Queue has been stopped.")`. Теперь `QueueStopped(Exception? cause = null)`
передаёт `ex` во внутреннее исключение: «ChannelClosedException» само по себе не говорит
ничего о том, почему очередь остановилась.

**L4 — `ShutdownError` хранит корневую причину.** `HandleShutdownOnError` присваивал поле
безусловно. `ShutdownAsync` при повторном вызове возвращается сразу (состояние уже занято),
поэтому каждый последующий сбой конкурировал за запись — и поздний, контекстный, вытеснял
исходный. Стало `_shutdownError ??= ex`. Единственное место записи в поле, гонка benign:
в худшем случае двое увидят `null`, и запишет один.

**L1 — отрицательный `ConcurrencyLimit` бросает.** Сеттер клампил в `0`, то есть в вечную
паузу, причём `IsEnabled` оставался `true` — ошибка без симптома, которую не видно ни в
логах, ни в телеметрии. Конструктор и `WithConcurrencyLimit` этот случай уже отвергали;
сеттер был единственной дырой. Клампом остался только случай «положительное значение выше
`MaxConcurrency»» — там ограничение осмысленно и отбрасывать значение нельзя.

**L7 — DI отклоняет не-Singleton.** `AddSaWorkQueue(configureOptions, lifetime)` принимал
`Scoped`/`Transient`, при которых каждое разрешение получает свой пул читателей и свою копию
буфера; readme это прямо запрещал, но запрет был словом, а не проверкой. Теперь бросается
`ArgumentOutOfRangeException` на регистрации. Второе перегрузка (`<TProcessor, TInput>`)
уже была жёстко Singleton, так что поведение стало одинаковым в обеих.

**L8 — отрицательный `MaxConcurrency` бросает.** `options.MaxConcurrency > 0 ? … :
ProcessorCount` молча заменял *любое* значение `< 1`. `WithMaxConcurrency` документирует `0`
как «процессоры», и `null` значит то же самое, поэтому `0`/`null` остались как есть — а вот
отрицательное значение не имеет ни одного прочтения и теперь отвергается конструктором, где
уже проверяются `ConcurrencyLimit`, `QueueCapacity` и `ShutdownTimeout`.

**D3 — `failIfPaused` → `failIfNoProgress`.** Под этим флагом бросались две разные ошибки:
`QueuePaused` (явная пауза с ожидающей работой) и `QueueHasNoReaders` (очередь активна, но
ридеров нет — например, после `ForceCancelReaders` или срабатываний `StopReader`). Для
вызывающего это одно и то же состояние — «работа есть, а взять её некому», — но имя обещало
только первое, и XML-док прямо утверждал обратное («потеря ридеров — не пауза, ожидание
продолжится»), тогда как код в этой ветке возвращался. Переименование вместо второго
параметра: две ошибки различаются только текстом сообщения, а различать их флагом — значит
заставить вызывающего знать внутренности. XML-док переписан под фактическое поведение.

**Совместимость.** D3 — единственное изменение, ломающее исходный код вызывающей стороны
(переименованный параметр). Версия пакета остаётся `0.12.0`: пакет пре-1.0, а в репозитории
версии поднимаются общим тикетом по всем пакетам, а не по одной правке. Остальные правки
ломают только то, что было ошибкой (отрицательные значения, не-Singleton, потерянное
исключение).

---

## Что осталось открытым

Закрытые находки (C1, D1, M4, L1, L3–L8, D2, D3) вынесены в раздел «Что сделано» выше.

### M1 — статус-колбэк под `_readersSync` (дедлок)

Воспроизведено: колбэк отдаёт работу в другой поток, которому нужен `_readersSync`, и ждёт
его. Поток, выставивший `ConcurrencyLimit`, держит лок → взаимоблокировка.

Сейчас это задокументировано в коде (см. D1), но **не исправлено**: фикс требует вынести запуск
петли за пределы `_readersSync` (собрать «отложенные старты» и запустить их после выхода из
лока), а это перестройка структуры локов — отдельное изменение со своим ревью. Шаг 5 плана;
делать последним, потому что задевает всё, что касается пула.

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
отмены токена вызывающего. (Это единственная работа, которая нужна по M2, и она состоит
только из документации — поведение менять не требуется.)

### M3 — дренаж при недождавшихся ридерах — ✅ закрыто

**Уточнение после разбора кода: находка была сформулирована шире, чем на самом деле.**
Первоначальный вариант утверждал, что `TimeoutException`/`AggregateException` пробрасываются
из обоих вариантов. Проверка показала:

| Метод | Ожидание | Таймаут | Может ли упасть? | Дренаж |
|---|---|---|---|---|
| `ForceCancelReaders` | `Task.WaitAll(tasks, ts)` | **возвращает `false`**, не бросает | нет — у ридера catch-all | всегда выполняется |
| `ForceCancelReadersAsync` | `WaitAsync(t, clock, ct)` | **бросает `TimeoutException`** | нет | пропускался |
| `Shutdown` / `ShutdownAsync` | `WaitAllBounded` / `WaitAsync` | ловят либо гасят | нет | в `finally` |

Два основания для «не может упасть»: `ReaderLoopAsync` имеет `catch (Exception)` на весь
цикл, поэтому задача ридера никогда не переходит в faulted — `AggregateException` из
`Task.WhenAll` невозможен в принципе. А `Task.WaitAll(tasks, timeout)` по контракту
возвращает `bool` и **`TimeoutException` не бросает** — в отличие от `WaitAsync`.

Дыра была ровно одна, в `ForceCancelReadersAsync`, и триггеров у неё два: истёкший
`timeout` и **отменённый `ct`**. Второго в исходной формулировке не было, а он опаснее:
вызывающий с короткоживущим токеном молча терял дренаж.

**Решение (продуктовое): не дренировать, убрать противоречие API.** Аргумент за этот
вариант: элементы в буфере никто не отклонял, и перевосстановленный пул может их взять —
выбрасывать работу, которую никто не просил выбрасывать, хуже. Синхронный
`ForceCancelReaders` при этом не тронут: у него «остановить пул и выбросить остаток» и
есть смысл операции, а явный `timeout` у вызывающего — утверждение о том, сколько ридеры
должны разворачиваться, а не разрешение выбросить очередь.

Что сделано:

1. `ForceCancelReadersAsync` ловит таймаут и отмену, **пишет предупреждение** с числом
   недокрученных ридеров и числом сохранённых элементов, и **перебрасывает** исключение —
   вызывающий узнаёт, что ожидание истекло, а не выдаёт это за успех.
2. `WaitForIdleAsync` на обеих ветках «прогресса не будет» пишет предупреждение с
   указанием причины (`Paused` / `NoReaders`).
3. Контракт расписан в `ISaWorkQueue.ForceCancelReadersAsync`: буфер сохраняется,
   `IsIdle()` честно `false`, и это не противоречит раннему возврату `WaitForIdleAsync`,
   потому что тот теперь объявляет причину.

`IsIdle() == false` + ранний возврат `WaitForIdleAsync` перестали быть противоречием: первое
отвечает «есть ли работа», второе — «ждать ли дальше», и второе теперь говорит вслух, что
ждать бессмысленно.

Тесты: `WorkQueueForceCancelDrainTests` (5). Сквозной тест доводит ситуацию до конца —
истёкшее ожидание, элемент в буфере не помечен как `Faulted`, оба API признают состояние,
перевосстановленный пул обрабатывает элемент. Откатом фикса падают три из пяти; два
оставшихся — намеренные стражи границы (дренаж без таймаута сохранён, на штатном пути
предупреждений нет), они зелёные в обе стороны.

**Побочная находка:** элемент, чей ридер был отменён, но процессор вернулся сам,
завершается как `Completed`, а не `Cancelled`. Не ошибка — работа реально была выполнена,
— но стоит знать при чтении статусов.

### L2 — сеттер меняет состояние после shutdown

`_concurrency` и `_paused` присваиваются до `if (IsEnabled)`, поэтому после остановки можно
«выставить» лимит, которого никогда не будет. Если это намеренно («запомнить настройку»),
стоит задокументировать; если нет — перенести внутрь `if`.

---

## Тесты

Новый файл `src/Tests/Sa.Utils.WorkQueue.Tests/WorkQueueSyncReaderTests.cs` — 10 тестов
на C1. Сценарий строится так, чтобы ридер гарантированно умирал синхронно: пауза,
непустой буфер, процессор без `await` до `throw`.

| Тест | Что закрывает |
|---|---|
| `ReArm_WithStopReader_KeepsTheRequestedLimit` | последствие 1: лимит 4 не съедается |
| `ReArm_WithDefaultShutdownQueueStrategy_KeepsTheRequestedLimit` | то же для пути по умолчанию |
| `ReArm_LeavesNoDeadReaderBehind` | последствие 2 + 5: список пуст, ни одного диспоузнутого CTS |
| `LimitDecrease_AfterSynchronousFault_DoesNotThrowObjectDisposedException` | последствие 3: `ObjectDisposedException` из сеттера |
| `WaitForIdle_AfterSynchronousFaultPool_ReapsInsteadOfHanging` | последствие 4: ожидание возвращается, а не висит |
| `WaitForIdle_FailIfNoProgress_ThrowsWhilePoolEmpty_AndRecoversAfterReArm` | D3: флаг видит пустой пул и корректно восстанавливается |
| `RepeatedReArm_DoesNotAccumulateDeadReaders` | последствие 2: 5 раундов, ни одной накопленной записи |
| `ReArm_WithSuspendingProcessor_StillRegistersTheReader` | «здоровая» ветка: ридер, который уступает поток, регистрируется как обычно |
| `ScaleUpAndDown_WithLiveReaders_IsUnaffected` | регрессия: обычное масштабирование 2→5→1→4 не сломано |
| `ReArm_WhileCancellingReaders_DoesNotLetThemEatTheNewLimit` | правило 5 из удалённого раздела readme: отменяющиеся ридеры не забирают слот нового лимита |

Вспомогательные хелперы в файле переписаны под новую структуру: `Readers` (рефлексия в
`_readers`), `Prop` (свойства вложенного типа `Reader` — `Loop`, `Work`, `IsLive`),
`TrackedCount`/`LiveCount`, `WaitForTrackedCountAsync` (поллинг до нужного числа — снятие
гонки с reaping'ом отменённых ридеров), `AssertNoDisposedReaders`. Хелперы `ReaderCts` и
`AssertNoDisposedReaders` опираются на имена приватных членов, поэтому переименование
`_readers` или `Loop`/`Work`/`IsLive` сломает их — это осознанный компромисс ради тестируемости.

Покрытия до фикса не было ни у одного из этих сценариев: `StopReader_ReducesConcurrency_NoAutoReplace`
проверяет незапланированный декремент из живого пула, `MultipleStopReaders_CorrectCapacityDecrease` —
конкурентные `StopReader`, а re-arm после синхронного падения не проверял никто.

### Тесты волны 2 — `WorkQueueDiagnosticsTests.cs`

Новый файл, 7 тестов на M4, L1, L3, L4, L7, L8. Смысл общий: **тест должен падать на старом
коде**, иначе он ничего не закрепляет. Все шесть правок проверены откатом — с возвратом
`_shutdownError = ex`, `OnStatusChanged(..., Aborted)` / `(..., Cancelled)` без `ex` и
`QueueStopped()` без причины падают ровно четыре теста; остальные проверяют бросок, который
сам по себе является утверждением.

| Тест | Что закрывает |
|---|---|
| `AbortedStatus_ReportsTheOperationCanceledException` | M4: колбэк получает OCE с токеном продюсера, не `null` |
| `CancelledStatus_ReportsTheOperationCanceledException` | M4: то же для статуса, инициированного очередью |
| `ParkedProducer_ReceivesTheCauseWithTheStoppedError` | L3: `InvalidOperationException.InnerException` — исходный `ChannelClosedException` |
| `ShutdownError_IsTheFaultThatStartedTheShutdown` | L4: базовый случай |
| `ShutdownError_KeepsTheRootCauseWhenALaterItemFaultsToo` | L4: главный случай — поздний сбой не вытесняет корневой |
| `AddSaWorkQueue_RejectsNonSingletonLifetime` | L7: `ArgumentOutOfRangeException`, коллекция остаётся пустой |
| `AddSaWorkQueue_AcceptsSingletonLifetime` | L7: не сломали нормальный путь, `TryAdd` даёт один инстанс |

Первый вариант `ShutdownError_KeepsTheRootCause…` был написан на четырёх подряд падающих
элементах и **проходил и на старом коде** — то есть ничего не проверял: после первого сбоя
следующие элементы обычно не доходили до процессора. Переписан так, чтобы порядок был
детерминированным: два элемента в полёте, первый падает сразу, второй игнорирует
отмену и падает только после того, как тест дождался записи `ShutdownError`. Синхронизация
по колбэку `HandleItemFaulted` была бы гонкой — запись происходит сразу после его возврата,
поэтому тест ждёт заполнения поля (`WaitForShutdownErrorAsync`).

Тесты к M2, M3, L2 **не написаны** — M3 требует сначала решить, какое поведение считать
правильным, а L2 — что считать правильным при присваивании лимита после shutdown. Для M2
поведение уже установлено (см. раздел выше): достаточно теста на две точки выхода, отличные от
токена вызывающего, чтобы зафиксировать текущую семантику без риска для совместимости.

Итог: **117/117** в `Sa.Utils.WorkQueue.Tests` (было 99), **133/133** в `Sa.ScheduleTests`.
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
