# Sa.Utils.WorkQueue

Высокопроизводительная асинхронная очередь задач для .NET с ограниченной ёмкостью, динамическим контролем параллелизма и несколькими стратегиями масштабирования читателей. Построена на базе `System.Threading.Channels`.

---

## Возможности

| Возможность | Описание |
|-------------|----------|
| **Ограниченная очередь** | Back-pressure через `BoundedChannel` — переполнение обрабатывается по `Wait`, `DropWrite` или `DropOldest` |
| **Динамический параллелизм** | Изменяйте `ConcurrencyLimit` на лету — читатели адаптируются автоматически |
| **Стратегии масштабирования** | `Lifo` • `Fifo` • `RoundRobin` • `Random` — выберите подход к замене читателей при ресайзе |
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
        .WithReaderScalingStrategy(SaReaderScalingStrategy.RoundRobin)
        .WithFullMode(BoundedChannelFullMode.DropOldestWhenFull)
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
    .WithFullMode(BoundedChannelFullMode)          // Wait | DropOldest | DropNewest | DropWrite
    .WithReaderScalingStrategy(enum)              // Lifo | Fifo | RoundRobin | Random
    .WithStatusCallback(Action<TInput, SaWorkStatus, Exception?>)
    .WithHandleItemFaulted(Func<TInput, Exception, SaExecutionErrorStrategy>)
    .WithItemDisplayName(Func<TInput, string>)    // Пользовательское имя элемента для логирования
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

## 🔑 API `ISaWorkQueue<TInput>`

| Член | Тип | Описание |
|------|-----|----------|
| `Enqueue(input, ct)` | Метод | Добавить задачу (не блокирует, если есть место) |
| `WaitForIdleAsync(ct)` | Метод | Дождаться завершения всех задач |
| `ShutdownAsync()` | Метод | Корректное завершение (финиш активных + очистка) |
| `Shutdown()` | Метод | Синхронное завершение |
| `ForceCancelReaders()` | Метод | Аварийная остановка всех читателей |
| `ForceCancelReadersAsync()` | Метод | Асинхронная аварийная остановка |
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
| `StopReader` | Пометить элемент как Faulted, остановить текущего читателя (автоматически заменится) |
| `ShutdownQueue` | Пометить элемент как Faulted, инициировать полное завершение очереди |

По умолчанию: `ShutdownQueue` — ошибка элемента запускает shutdown. Для отказоустойчивых пайплайнов переопределите на `Continue` или `StopReader`.

---

## ⚠️ Важные заметки

1. **Жизненный цикл**: регистрируется как `Singleton`. Не используйте `Scoped`/`Transient`.
2. **Callback `StatusChanged`**: вызывается синхронно на thread-pool потоке. Избегайте длительных операций внутри. Исключения обработчика логируются, но не распространяются.
3. **Отмена**: каждый `Enqueue` принимает `CancellationToken`. Элементы различают отмену вызывающей стороной (`Aborted`) и системную отмену (`Cancelled`).
4. **Потокобезопасность**: все публичные члены потокобезопасны. Изменение `ConcurrencyLimit` на лету корректирует число читателей без потери ожидающих элементов.
5. **Идемпотентное завершение**: `ShutdownAsync`, `Shutdown`, `Dispose`, `DisposeAsync` безопасны для многократного вызова.
6. **`ConcurrencyLimit = 0`**: приостанавливает всю обработку (убивает всех читателей). Верните положительное значение для возобновления.
7. **`ForceCancelReaders` / `ForceCancelReadersAsync`**: аварийная остановка — мгновенно отменяет все reader-задачи. После вызова восстановите параллелизм установкой `ConcurrencyLimit = X` для запуска новых читателей.
8. **Back-pressure**: при заполненной очереди поведение зависит от `FullMode` — `Wait` блокирует вызывающего, `DropOldest` удаляет самый старый элемент, `DropNewest` отбрасывает входящий, `DropWrite` завершает вызов enqueue ошибкой.
