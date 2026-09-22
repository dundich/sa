# Sa.Utils.WorkQueue

Высокопроизводительная асинхронная очередь задач для .NET с ограниченной ёмкостью, динамическим контролем параллелизма и несколькими стратегиями масштабирования читателей. Построена на базе `System.Threading.Channels`.

---


# 📄 Архитектурное описание: `SaWorkQueue<TInput>`

## 1. Общее назначение
`SaWorkQueue<TInput>` — это высокопроизводительная, потокобезопасная очередь фоновой обработки задач, построенная на базе паттерна **Producer-Consumer** с использованием `System.Threading.Channels`. Класс предназначен для асинхронного выполнения задач (`ISaWork<TInput>`) с динамическим управлением пулом рабочих потоков (читателей), обработкой ошибок и отслеживанием состояния простоя.

## 2. Ключевые архитектурные принципы

### А. Динамическая емкость пула (`ConcurrencyLimit`)
* **Семантика:** Свойство `ConcurrencyLimit` отражает **текущую фактическую емкость пула** (количество физически существующих и готовых к работе читателей), а не просто статическую "целевую настройку".
* **Поведение при сбоях:** Если читатель завершается (например, из-за стратегии `SaExecutionErrorStrategy.StopReader` или вызова `ForceCancelReaders`), значение `ConcurrencyLimit` **уменьшается** атомарно (`Interlocked.Decrement`). Это гарантирует, что свойство всегда отражает реальное доступное количество рабочих единиц.
* **Восстановление:** Если читатель завершился (стратегия `StopReader`, `ForceCancelReaders`, краш), ёмкость пула уменьшается. Для восстановления установите `ConcurrencyLimit` в целевое значение — сеттер запустит недостающих читателей.

### Б. Точный учет задач (`_taskCount` и `IsIdle`)
* **Единственный источник истины:** Метод `IsIdle()` опирается **исключительно** на значение `volatile int _taskCount == 0`.
* **Почему так:** Проверка `_queue.Reader.Count` подвержена состояниям гонки (race conditions) между моментом извлечения задачи из канала и моментом начала её выполнения. Счетчик `_taskCount` инкрементируется в `MarkActive()` *до* записи в канал и декрементируется в `MarkInactive()` *после* завершения обработки, что дает 100% точный сигнал о простое через `TaskCompletionSource` (`_idleTcs`).
* **Защита от отрицательных значений:** `MarkInactive()` содержит проверку `if (_taskCount > 0)`, чтобы предотвратить уход счетчика в минус при гонке между `Enqueue` и `Shutdown`.

### В. Детерминированная принудительная остановка (`ForceCancelReaders`)
* Методы `ForceCancelReaders` и `ForceCancelReadersAsync` не просто отправляют сигнал отмены (`Cancel()`), но и **гарантированно ожидают** физического завершения задач читателей (`Task.WaitAll` / `Task.WhenAll`).
* Это критически важно: только после этого ожидания гарантируется, что `_ctsReaders.Count` и `_concurrency` уже корректно обновлены методами `RemoveReader`, и последующие операции (например, изменение `ConcurrencyLimit`) работают с актуальным состоянием.

### Г. Режимы отмены читателей (`SaReaderCancelMode`)
* У каждого читателя два CTS, оба линкованы на `_shutdownCts`: **loop CTS** (получение items, gate цикла) и **work CTS** (токен, передаваемый в `ISaWork<TInput>.Execute`).
* **`Hard` (по умолчанию):** work-токен линкован на loop CTS — отмена читателя немедленно отменяет его текущий item (`Cancelled`).
* **`Soft`:** work-токен линкован на work CTS — при уменьшении лимита или паузе (`ConcurrencyLimit = 0`) читатель удаляется **без прерывания** текущего item: он доделывается штатно (`Completed`), а оставшиеся в канале элементы продолжают ждать оставшихся или новых читателей.
* **Пути эскалации всегда жёсткие в обоих режимах:** `ForceCancelReaders(Async)` дополнительно отменяет work CTS читателя, а `Shutdown`/`Dispose` отменяют `_shutdownCts` — in-flight работа там всегда прерывается (с ограничением `ShutdownTimeout`).

## 3. Основные компоненты

| Компонент | Назначение |
| :--- | :--- |
| `Channel<WorkItem> _queue` | Потоконебезопасный буфер для передачи задач от производителей к потребителям. Настроен на `SingleReader = false` (множественные потребители). |
| `_ctsReaders` / `_ctsWorks` / `_taskReaders` | Отслеживают фактическое количество запущенных циклов обработки (`ReaderLoopAsync`): loop CTS, work CTS (Soft-режим / эскалация Force) и задача читателя. Хранятся в параллельном порядке. |
| `_concurrency` | Атомарное поле, хранящее текущую емкость пула. Синхронизировано с `_ctsReaders.Count` при штатной работе, но управляется через `Interlocked` при аварийном завершении. |
| `_taskCount` / `_idleTcs` | Механизм эффективного ожидания простоя очереди без активного опроса (polling). |
| `_state` (`QueueState`) | Конечный автомат состояний: `Active` (0), `Shutdown` (1), `Disposed` (2). Управляется через `Interlocked.CompareExchange`. |

## 4. Обработка ошибок и стратегии (`SaExecutionErrorStrategy`)

При исключении в `ISaWork<TInput>.Execute` срабатывает делегат `_handleItemFaulted`, который возвращает одну из стратегий:
1. **`Continue`**: Задача помечается как `Faulted`, но читатель продолжает брать следующие задачи из канала.
2. **`StopReader`**: Задача помечается как `Faulted`, текущий читатель завершает свой цикл (`return false`), вызывая `RemoveReader`. **Емкость пула (`ConcurrencyLimit`) уменьшается на 1.** Остальные читатели продолжают работу.
3. **`ShutdownQueue`**: Инициируется асинхронное завершение работы всей очереди (`ShutdownAsync`), все новые задачи будут отвергнуты.

## 5. Жизненный цикл и завершение работы

* **Graceful Shutdown (`Shutdown` / `ShutdownAsync`)**:
  1. Переводит состояние в `Shutdown`.
  2. Отменяет `_shutdownCts` (сигнал читателям завершиться после текущей задачи).
  3. Закрывает канал для записи (`TryComplete()`).
  4. Ожидает завершения всех задач читателей (`WaitForReadersToCompleteAsync`).
  5. Очищает оставшиеся в канале задачи (`DrainAndResetIdle`), помечая их как `Faulted` и сбрасывая `_taskCount` в 0 для корректного `IsIdle()`.
* **Dispose**: Гарантирует вызов `Shutdown` и освобождение `_shutdownCts`. Повторные вызовы безопасны (идемпотентны).

---

## Возможности

| Возможность | Описание |
|-------------|----------|
| **Ограниченная очередь** | Back-pressure через `BoundedChannel` — при переполнении вызывающий блокируется (`Wait`) |
| **Динамический параллелизм** | Изменяйте `ConcurrencyLimit` на лету — читатели адаптируются автоматически |
| **Стратегии масштабирования** | `Lifo` • `Fifo` • `RoundRobin` • `Random` — выберите подход к замене читателей при ресайзе |
| **Режимы отмены** | `Hard` (по умолчанию) или `Soft` — как обрабатывается in-flight работа при удалении читателей |
| **DI-интеграция** | `AddSaWorkQueue<TProcessor, TInput>` с полной поддержкой конфигурации |
| **Логирование без аллокаций** | `[LoggerMessage]` source generator для `ILogger` |
| **Корректное завершение** | `ShutdownAsync`, `DisposeAsync` — идемпотентно и потокобезопасно |
| **Стратегии ошибок** | `Continue` (по умолчанию на элемент), `StopReader` или `ShutdownQueue` |
| **Обратные вызовы статусов** | Отслеживайте жизненный цикл: `Running` → `Completed` / `Faulted` / `Cancelled` / `Aborted` |

---

## 🚀 Быстрый старт

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
    {
        await queue.Enqueue(order, ct);
    }

    public async Task WaitForCompletionAsync(CancellationToken ct)
        => await queue.WaitForIdleAsync(ct);

    public bool IsIdle() => queue.IsIdle();
    public int Pending => queue.QueueTasks;
}
```

---

## ⚙️ Настройка `SaWorkQueueOptions<TInput>`

Все параметры — immutable record поля с fluent-методами `With*`:

```csharp
SaWorkQueueOptions<TInput>.Create(processor)
    .WithConcurrencyLimit(int)                    // Параллельных читателей (по умолч.: кол-во ядер)
    .WithQueueCapacity(int)                       // Ёмкость канала (по умолч.: равно лимиту)
    .WithMaxConcurrency(int)                      // Абсолютный потолок читателей (по умолч.: кол-во ядер)
    .WithSingleWriter(bool)                       // Оптимизация для однопользовательских сценариев
    .WithReaderCancellationOrder(enum)             // Lifo | Fifo | RoundRobin | Random
    .WithReaderCancelMode(enum)                   // Hard | Soft — in-flight работа при удалении читателя
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

## Стратегии масштабирования читателей

Применяются при уменьшении `ConcurrencyLimit` на лету — определяют, каких читателей отменять:

| Стратегия | Поведение | Лучше всего для |
|-----------|----------|-----------------|
| `Lifo` | Отменяет наиболее недавних читателей | CPU-bound задачи, локальность кэша |
| `Fifo` | Отменяет самых старых читателей | Ресурсная ротация, равномерное время жизни |
| `RoundRobin` | Циклический обход читателей | Стабильные воркеры, сбалансированная нагрузка |
| `Random` | Случайный выбор читателей | Тестирование, избегание паттернов |

---

## Режимы отмены читателей

Управляют судьбой in-flight работы при удалении читателей на лету (уменьшение лимита, или пауза через `ConcurrencyLimit = 0`). Настраивается через `.WithReaderCancelMode(...)`:

| Режим | Поведение | Лучше всего для |
|-------|----------|-----------------|
| `Hard` (по умолчанию) | Текущий item немедленно отменяется (`Cancelled`) и теряется | Быстрая остановка, дёшево прерываемая работа |
| `Soft` | Удаляемый читатель доделывает текущий item (`Completed`) перед выходом; оставшиеся в канале элементы продолжают ждать | Дорогая в прерывании работа (I/O, вызовы сторонних API, длительные вычисления) |

`ForceCancelReaders` / `ForceCancelReadersAsync` и `Shutdown` / `ShutdownAsync` **всегда** прерывают in-flight работу независимо от режима.

---

## 🔑 API `ISaWorkQueue<TInput>`

| Член | Тип | Описание |
|------|-----|----------|
| `Enqueue(input, ct)` | Метод | Добавить задачу (не блокирует, если есть место) |
| `WaitForIdleAsync(ct)` | Метод | Дождаться завершения всех задач |
| `ShutdownAsync()` | Метод | Корректное завершение (финиш активных + очистка) |
| `Shutdown()` | Метод | Синхронное завершение |
| `ForceCancelReaders()` | Метод | Аварийная остановка всех читателей (ограниченное ожидание) |
| `ForceCancelReadersAsync(timeout, ct)` | Метод | Асинхронная аварийная остановка с опциональным таймаутом |
| `IsIdle()` | Свойство | `true`, если нет ожидающих/активных задач |
| `IsEnabled` | Свойство | `true`, пока очередь активна |
| `QueueTasks` | Свойство | Всего задач в обработке + в очереди |
| `ConcurrencyLimit` | Свойство | Текущий лимит параллелизма (изменяемый) |
| `MaxConcurrency` | Свойство | Абсолютный потолок |
| `QueueCapacity` | Свойство | Ёмкость ограниченного канала |
| `ShutdownError` | Свойство | Исключение, вызвавшее shutdown, если было |

---

## Жизненный цикл статусов

Каждый элемент проходит через статусы, которые сообщаются через callback `StatusChanged`:

| Статус | Значение |
|--------|----------|
| `Running` | Элемент обрабатывается |
| `Completed` | Успешно завершён |
| `Faulted` | Произошла необработанная ошибка |
| `Cancelled` | Отменён системой (shutdown, таймаут) |
| `Aborted` | Отменён явно токеном вызывающего |

---

## Стратегии обработки ошибок

Настраиваются через `.WithHandleItemFaulted(...)`:

| Стратегия | Поведение |
|-----------|----------|
| `Continue` | Пометить элемент как Faulted, продолжить обработку остальных |
| `StopReader` | Пометить элемент как Faulted, остановить текущего читателя (ёмкость уменьшается; восстановите через `ConcurrencyLimit = X`) |
| `ShutdownQueue` | Пометить элемент как Faulted, инициировать полное завершение очереди |

По умолчанию: `ShutdownQueue` — ошибка элемента запускает shutdown. Для отказоустойчивых пайплайнов переопределите на `Continue` или `StopReader`.

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
