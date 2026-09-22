# Sa.Utils.WorkQueue

Высокопроизводительная асинхронная очередь задач для .NET с ограниченной ёмкостью, динамическим контролем параллелизма и типобезопасным API. Построена на базе `System.Threading.Channels`.

---

## Возможности

| Возможность | Описание | Детали |
|-------------|----------|--------|
| **Ограниченный параллелизм** | Контроль числа одновременно выполняющих задач через `ConcurrencyLimit` | [Параллелизм и масштабирование](#параллелизм-и-масштабирование) |
| **Динамический параллелизм** | Изменяйте `ConcurrencyLimit` на лету — читатели адаптируются автоматически | [Параллелизм и масштабирование](#параллелизм-и-масштабирование) |
| **Back-pressure** | `BoundedChannel` — стратегия `Enqueue` при полном буфере: `Wait` (по умолчанию, блокирует) • `Skip` (возвращает `false`) • `Throw` (`SaWorkQueueFullException`); плюс неблокирующий `TryEnqueue` и пакетный `EnqueueMany` | [Стратегии Enqueue](#стратегии-enqueue) |
| **Порядок отмены читателей** | `Lifo` • `Fifo` • `RoundRobin` • `Random` — выберите, каких читателей отменять при уменьшении лимита | [Порядок отмены читателей](#порядок-отмены-читателей) |
| **Режимы отмены** | `Hard` (по умолчанию) или `Soft` — как обрабатывается in-flight работа при удалении читателей | [Режимы отмены читателей](#режимы-отмены-читателей) |
| **DI-интеграция** | Регистрация через `AddSaWorkQueue<TProcessor, TInput>` или делегатную `AddSaWorkQueue<TInput>` | [Быстрый старт](#быстрый-старт) |
| **Логирование без аллокаций** | `[LoggerMessage]` source generator для `ILogger` | — |
| **Корректное завершение** | `ShutdownAsync`, `DisposeAsync` — идемпотентно и потокобезопасно | [Важные заметки](#-важные-заметки) |
| **Стратегии ошибок** | Обработка сбоев элемента: `Continue`, `StopReader` или `ShutdownQueue` (по умолчанию) | [Стратегии обработки ошибок](#стратегии-обработки-ошибок) |
| **Обратные вызовы статусов** | Отслеживайте жизненный цикл: `Running` → `Completed` / `Faulted` / `Cancelled` / `Aborted` / `Skipped` | [Жизненный цикл статусов](#жизненный-цикл-статусов) |

---

## Быстрый старт

### 1️⃣ Реализуйте процессор

```csharp
public sealed class OrderWork(ILogger<OrderWork> logger) : ISaWork<OrderInput>
{
    public async Task Execute(OrderInput input, CancellationToken ct)
    {
        logger.LogInformation("Processing order {OrderId}", input.OrderId);
        await ProcessOrderAsync(input, ct); // Ваша бизнес-логика
    }
}
```

### 2️⃣ Зарегистрируйте в DI

```csharp
builder.Services.AddSaWorkQueue<OrderWork, OrderInput>((sp, opts) =>
    opts
        .WithConcurrencyLimit(4)
        .WithQueueCapacity(100)
        .WithMaxConcurrency(16)
        .WithReaderCancellationOrder(SaReaderCancellationOrder.RoundRobin)
        .WithStatusCallback((input, status, ex) =>
        {
            // logger.LogDebug("Заказ {Id} → {Status}", input.OrderId, status);
        }));
```

### 3️⃣ Используйте через внедрение

```csharp
public class OrderService(ISaWorkQueue<OrderInput> queue)
{
    public async Task SubmitAsync(OrderInput order, CancellationToken ct)
        => await queue.Enqueue(order, ct);

    public async Task WaitForCompletionAsync(CancellationToken ct)
        => await queue.WaitForIdleAsync(ct);

    public bool IsIdle() => queue.IsIdle();
    public int Pending => queue.QueueTasks;
}
```

---

## Настройка `SaWorkQueueOptions<TInput>`

Все параметры — immutable record поля с fluent-методами `With*`:

```csharp
SaWorkQueueOptions<TInput>.Create(processor)
    .WithConcurrencyLimit(int)                    // Параллельных читателей (по умолч.: кол-во ядер)
    .WithQueueCapacity(int)                       // Ёмкость канала (по умолч.: равно MaxConcurrency)
    .WithMaxConcurrency(int)                      // Абсолютный потолок читателей (по умолч.: кол-во ядер)
    .WithSingleWriter(bool)                       // Оптимизация для сценариев с одним писателем
    .WithReaderCancellationOrder(enum)             // Lifo | Fifo | RoundRobin | Random
    .WithReaderCancelMode(enum)                   // Hard | Soft — in-flight работа при удалении читателя
    .WithEnqueueStrategy(enum)                    // Wait | Skip | Throw — поведение при полном буфере
    .WithStatusCallback(Action<TInput, SaWorkStatus, Exception?>)
    .WithHandleItemFaulted(Func<TInput, Exception, SaExecutionErrorStrategy>)
    .WithItemDisplayName(Func<TInput, string>)    // Пользовательское имя элемента для логирования
    .WithShutdownTimeout(TimeSpan)                 // Макс. ожидание читателей при shutdown/force-cancel (по умолч.: 30с)
```

### Создание опций

```csharp
// Через реализацию ISaWork<TInput>
var opts = SaWorkQueueOptions<OrderInput>.Create(new OrderWork(logger));

// Через делегат (без класса)
var opts = SaWorkQueueOptions<OrderInput>.Create(async (input, ct) => {
    await ProcessAsync(input, ct);
});
```

---

## Параллелизм и масштабирование

Очередь выполняет задачи параллельно не более чем `ConcurrencyLimit` читателями (по умолчанию: количество ядер CPU), не превышая `MaxConcurrency`. Лимит изменяется на лету — читатели запускаются или отменяются по мере необходимости, ожидающие элементы при этом не теряются.

```csharp
// Конфигурация
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithConcurrencyLimit(4)      // стартовое число читателей
    .WithMaxConcurrency(16);      // потолок для изменения на лету
    .WithQueueCapacity(100);      // размер ограниченного буфера
```

```csharp
// Масштабирование на лету
queue.ConcurrencyLimit = 8;  // запустить больше читателей
queue.ConcurrencyLimit = 4;  // отменить читателей (по настроенному порядку отмены)
queue.ConcurrencyLimit = 0;  // приостановить обработку; верните положительное значение для возобновления
```

Если читатель завершился сам (стратегия ошибок `StopReader`, `ForceCancelReaders`), `ConcurrencyLimit` уменьшается соответственно — верните целевое значение, чтобы восстановить пул.

---

## Порядок отмены читателей

Определяет, каких читателей отменять при уменьшении `ConcurrencyLimit` на лету (по умолчанию: `Lifo`).

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithReaderCancellationOrder(SaReaderCancellationOrder.RoundRobin);
```

| Стратегия | Поведение | Лучше всего для |
|-----------|----------|-----------------|
| `Lifo` (по умолчанию) | Отменяет наиболее недавних читателей | CPU-bound задачи, локальность кэша |
| `Fifo` | Отменяет самых старых читателей | Ресурсная ротация, равномерное время жизни |
| `RoundRobin` | Циклический обход читателей | Стабильные воркеры, сбалансированная нагрузка |
| `Random` | Случайный выбор читателей | Тестирование, избегание паттернов |

---

## Режимы отмены читателей

Управляют судьбой in-flight работы при удалении читателей на лету (уменьшение лимита, или пауза через `ConcurrencyLimit = 0`).

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithReaderCancelMode(SaReaderCancelMode.Soft);
```

| Режим | Поведение | Лучше всего для |
|-------|----------|-----------------|
| `Hard` (по умолчанию) | Текущий item немедленно отменяется (`Cancelled`) и теряется | Быстрая остановка, дёшево прерываемая работа |
| `Soft` | Удаляемый читатель доделывает текущий item (`Completed`) перед выходом; оставшиеся в канале элементы продолжают ждать | Дорогая в прерывании работа (I/O, вызовы сторонних API, длительные вычисления) |

`ForceCancelReaders` / `ForceCancelReadersAsync` и `Shutdown` / `ShutdownAsync` **всегда** прерывают in-flight работу независимо от режима.

---

## Стратегии Enqueue

`Enqueue` возвращает `ValueTask<bool>` и при полном буфере ведёт себя согласно настроенной `SaEnqueueStrategy`:

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithEnqueueStrategy(SaEnqueueStrategy.Wait);   // Wait | Skip | Throw
```

| Стратегия | Поведение при полном буфере | Лучше всего для |
|-----------|------------------------------|-----------------|
| `Wait` (по умолчанию) | Блокирует до освобождения места, затем возвращает `true` | Производители, которым нужен back-pressure |
| `Skip` | Бросает элемент и возвращает `false` (не учитывается в `QueueTasks`; сообщается как `Skipped`) | Fire-and-forget производители: метрики, телеметрия, логи |
| `Throw` | Бросает `SaWorkQueueFullException` (несёт `QueueCapacity`, `QueuedCount` и отображаемое имя элемента) | Производители, которым нужна явная реакция на перегрузку |

```csharp
var accepted = await queue.Enqueue(input, ct);
if (!accepted)
{
    // Режим Skip: буфер был полон, элемент отброшен (сообщён как Skipped)
}

// Свободные слоты до переполнения (информационно)
int free = queue.AvailableCapacity;
```

**Остановленная или disposed** очередь всегда бросает исключение (`InvalidOperationException` / `ObjectDisposedException`) независимо от стратегии. Так как `Enqueue` — async-метод, исключение несёт возвращённый `ValueTask` — наблюдайте его через `await`.

`TryEnqueue(input)` — неблокирующая версия, не зависящая от стратегии: всегда выполняет один try-write — `true`, если элемент принят, `false`, если буфер полон (элемент отброшен, не учитывается и сообщается как `Skipped`). Используйте на hot-путях, где вызывающий код не может блокироваться или делать `await`. Остановленная или disposed очередь всё равно бросает исключение — только теперь синхронно.

`EnqueueMany(inputs, ct)` добавляет несколько элементов одним вызовом и возвращает число принятых (`ValueTask<int>`). Каждый элемент ведёт себя согласно настроенной стратегии: `Wait` блокирует до освобождения места, так что в итоге принимаются все элементы; `Skip` бросает элементы, которым не хватило места, сообщая каждый из них как `Skipped`; `Throw` бросает `SaWorkQueueFullException` сразу, как только буфер наполняется, — с `AcceptedCount` и `TotalCount`. Элементы, принятые до сбоя `Throw`, остаются в очереди.

---

## Стратегии обработки ошибок

Управляют поведением при исключении в `ISaWork<TInput>.Execute`.

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithHandleItemFaulted((input, ex) =>
        ex is OutOfMemoryException
            ? SaExecutionErrorStrategy.ShutdownQueue
            : SaExecutionErrorStrategy.Continue);
```

| Стратегия | Поведение |
|-----------|----------|
| `Continue` | Пометить элемент как `Faulted`, продолжить обработку остальных |
| `StopReader` | Пометить элемент как `Faulted`, остановить текущего читателя (ёмкость уменьшается; восстановите через `ConcurrencyLimit = X`) |
| `ShutdownQueue` (по умолчанию) | Пометить элемент как `Faulted`, инициировать полное завершение очереди |

Для отказоустойчивых пайплайнов переопределите дефолт на `Continue` или `StopReader`.

---

## Жизненный цикл статусов

Каждый элемент проходит через статусы, которые сообщаются через callback `StatusChanged`:

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithStatusCallback((input, status, ex) =>
    {
        // например: logger.LogDebug("Заказ {Id} → {Status}", input.OrderId, status);
    });
```

| Статус | Значение |
|--------|----------|
| `Running` | Элемент обрабатывается |
| `Completed` | Успешно завершён |
| `Faulted` | Произошла необработанная ошибка |
| `Cancelled` | Отменён системой (shutdown, таймаут) |
| `Aborted` | Отменён явно токеном вызывающего |
| `Skipped` | Не принят, так как буфер был полон (стратегия `Skip`, `TryEnqueue` или `EnqueueMany`); элемент не обрабатывался |

---

## API — `ISaWorkQueue<TInput>`

| Член | Тип | Описание |
|------|-----|----------|
| `Enqueue(input, ct)` | Метод | Добавить задачу; возвращает `false` только в режиме `Skip` при полном буфере |
| `TryEnqueue(input)` | Метод | Неблокирующий enqueue; возвращает `false` при полном буфере (не зависит от стратегии) |
| `EnqueueMany(inputs, ct)` | Метод | Пакетный enqueue; возвращает число принятых элементов (каждый ведёт себя согласно стратегии) |
| `WaitForIdleAsync(ct)` | Метод | Дождаться завершения всех задач (немедленно, если очередь на паузе, `ConcurrencyLimit = 0`) |
| `ShutdownAsync()` | Метод | Завершение через отмену: отменяет всех читателей (in-flight работа прерывается, а не доделывается), ожидает читателей с ограничением `ShutdownTimeout`, дренаж остатка буфера как `Faulted` |
| `Shutdown()` | Метод | Синхронное завершение (блокирует вызвающий поток) |
| `ForceCancelReaders()` | Метод | Аварийная остановка всех читателей (ограниченное ожидание) |
| `ForceCancelReadersAsync(timeout, ct)` | Метод | Асинхронная аварийная остановка с опциональным таймаутом |
| `IsIdle()` | Метод | `true`, если нет ожидающих/активных задач |
| `IsEnabled` | Свойство | `true`, пока очередь активна |
| `QueueTasks` | Свойство | Всего задач в обработке + в очереди |
| `ConcurrencyLimit` | Свойство | Текущий лимит параллелизма (изменяемый) |
| `MaxConcurrency` | Свойство | Абсолютный потолок |
| `QueueCapacity` | Свойство | Ёмкость ограниченного канала |
| `AvailableCapacity` | Свойство | Свободные слоты буфера (информационно) |
| `ShutdownError` | Свойство | Исключение, вызвавшее shutdown, если было |

---

## ⚠️ Важные заметки

1. **Жизненный цикл**: регистрируется как `Singleton`. Не используйте `Scoped`/`Transient`.
2. **Callback `StatusChanged`**: вызывается синхронно на thread-pool потоке. Избегайте длительных операций внутри. Исключения обработчика логируются, но не распространяются.
3. **Отмена**: каждый `Enqueue` принимает `CancellationToken`. Элементы различают отмену вызывающей стороной (`Aborted`) и системную отмену (`Cancelled`).
4. **Потокобезопасность**: все публичные члены потокобезопасны. Изменение `ConcurrencyLimit` на лету корректирует число читателей без потери ожидающих элементов.
5. **Идемпотентное завершение**: `ShutdownAsync`, `Shutdown`, `Dispose`, `DisposeAsync` безопасны для многократного вызова.
6. **`ConcurrencyLimit = 0`**: приостанавливает всю обработку (отменяет всех читателей; в `Soft`-режиме текущие items сначала доделываются). Верните положительное значение для возобновления.
7. **`ForceCancelReaders` / `ForceCancelReadersAsync`**: аварийная остановка — мгновенно отменяет все reader-задачи. Синхронная версия ожидает до `ShutdownTimeout` (по умолч. 30с) завершения читателей; асинхронная принимает опциональный `TimeSpan? timeout`. После вызова восстановите параллелизм установкой `ConcurrencyLimit = X` для запуска новых читателей.
8. **Делегатная регистрация**: `AddSaWorkQueue<TInput>(configureOptions)` принимает фабрику, возвращающую `SaWorkQueueOptions<TInput>`, позволяя регистрировать очередь без класса `ISaWork<TInput>`.
9. **Возврат `Enqueue`**: `ValueTask<bool>` — `false` только в режиме `Skip` при полном буфере (элемент отброшен и сообщается как `Skipped` через `StatusChanged`). Остановленная/disposed очередь всегда бросает исключение; оно несётся `ValueTask` и раскрывается через `await`.
10. **`AvailableCapacity`**: только информационно — свободные слоты буфера. Не влияет на `IsIdle()`.

---

## 🤖 Критические правила для AI-Агентов (⚠️ ВАЖНО)

Эти правила ОБЯЗАТЕЛЬНО соблюдать при модификации этого кода:

1. **НИКОГДА** не использовать прямое присваивание `_concurrency = _ctsReaders.Count;`. Изменение ёмкости пула при удалении читателя выполняется через декремент `_concurrency` внутри `RemoveReader` (под `_readersSync`), чтобы избежать состояний гонки.
2. **НИКОГДА** не полагаться на `_queue.Reader.Count` для определения `IsIdle()`. Использовать только `_taskCount == 0`.
3. При добавлении новых методов принудительного прерывания всегда добавляйте ожидание завершения `Task` (`Task.WhenAll`), чтобы состояние счетчиков успело синхронизироваться.
4. Сеттер `ConcurrencyLimit` вычисляет дельту от фактического числа живых читателей (`_ctsReaders.Count - _pendingRemovals`), а не от настроенного `_concurrency` — это исключает «лишние» спавны/отмены для уже отменённых читателей.
5. `RemoveReader` вызывается в `finally` блока `ReaderLoopAsync`, который выполняется **после** `MarkInactive()` (в finally `ExecuteItemAsync`). Это означает, что `WaitForIdleAsync` может вернуть управление до завершения `RemoveReader`. Не считайте, что `_concurrency` полностью обновлён сразу после `WaitForIdleAsync`.
6. Все мутации списков читателей (`_ctsReaders`, `_ctsWorks`, `_taskReaders`, `_pendingRemovals`, `_intentionalRemovals`, `_forceCancelled`) должны выполняться под `lock (_readersSync)`. Три параллельных списка удаляются в `RemoveReader` по одному индексу — не рассинхронизируйте их.
7. Все мутации счётчиков задач (`_taskCount`, `_idleTcs`) должны выполняться под `lock (_wiSync)`.
