# Delivery

## Обзор

В этой папке находится вся **сторона доставки** (потребления) исключающего почтового ящика: от фоновой задачи, которая будит группу потребителей, до пообъектного учёта статусов и решений о ретраях и DLQ. Папка не зависит от хранилища — SQL живёт в `Sa.Outbox.PostgreSql`, а здесь работа идёт только через `IOutboxDeliveryManager` / `IOutboxContextFactory`.

Весь конвейер состоит из трёх слоёв:

| Слой | Тип | Ответственность |
|---|---|---|
| Цикл | `DeliveryProcessor` | Обходит тенанты, пока бэклог не опустеет или не исчерпан бюджет итераций |
| Проход по тенанту | `DeliveryTenant` | Арендует батч, удерживает блокировку, отдаёт сообщения потребителю, записывает результат |
| Сообщение | `DeliveryCourier` | Вызывает потребителя, превращает исключения в статусы, применяет политику приёма |

## Структура файлов

| Файл | Назначение |
|---|---|
| `IDeliveryBuilder.cs` / `DeliveryBuilder.cs` | Fluent-регистрация групп потребителей (`AddDelivery` / `AddDeliveryScoped`) |
| `Job/Setup.cs` | `AddDeliveryJob` — строит настройки, регистрирует потребителя по ключу, подключает задачу Sa.Schedule |
| `Job/DeliveryJob.cs` | `DeliveryJob<TMessage>` — точка входа `IJob`; находит настройки по имени задачи и вызывает `ProcessMessages` |
| `Job/DeliveryJobInterceptor.cs` | Интерцептор Sa.Schedule, навешенный на каждую задачу доставки |
| `IDeliveryProcessor.cs` / `DeliveryProcessor.cs` | Жадный цикл по тенантам; владеет учётом итераций и батчей |
| `IDeliveryTenant.cs` / `DeliveryTenant.cs` | Один проход по тенанту: фильтр → размер батча → аренда → доставка → возврат |
| `IDeliveryCourier.cs` / `DeliveryCourier.cs` | Вызывает потребителя, превращает исключения в `Warn`, применяет политику постобработки |
| `IDeliveryLifetimeInvoker.cs` / `DeliveryLifetimeInvoker.cs` | Разрешает `IConsumer<TMessage>` — один закешированный синглтон или новый DI-scope на батч |
| `IDeliveryBatcher.cs` / `DeliveryBatcher.cs` | Калькулятор размера батча по умолчанию (заглушка, возвращает `MaxBatchSize`) |
| `DeliveryStatusCode.cs` | Шкала статусов и классификаторы `IsPending` / `IsSuccess` / `IsWarning` / `IsError` |
| `DeliveryStatus.cs` | `readonly record struct`, описывающий результат одной попытки доставки |
| `OutboxContext.cs` | `IOutboxContextOperations<TMessage>` — API, которым потребитель принимает, ретраит или отклоняет сообщение |
| `IOutboxContextFactory.cs` / `OutboxContextFactory.cs` | Создаёт контексты для арендованных строк |
| `OutboxConsumerSettings.cs` | Неизменяемый снимок настроек (`record`) — единственный источник истины для группы потребителей |
| `OutboxConsumerSettingsBuilder.cs` | Fluent-билдер, используемый и при старте, и для частичных изменений во время работы |
| `OutboxDefaults.cs` | Значения по умолчанию для всех настроек в одном месте |
| `IOutboxConsumerManager.cs` / `OutboxConsumerManager.cs` | Потокобезопасный реестр времени выполнения: регистрация / изменение / пауза / возобновление / подписка |
| `IDeliverySnapshot.cs` / `DeliverySnapshot.cs` | Лениво материализуемое представление задач, частей сообщений и настроек потребителей (используется на этапе загрузки outbox) |
| `IRetryStrategy.cs` / `ExponentialBackoffRetryStrategy.cs` | Экспоненциальный backoff с джиттером по умолчанию: `min(maxDelay, baseDelay * 2^(attempt-1)) * jitter` |
| `FilterFactory.cs` | Собирает `OutboxMessageFilter` из настроек |
| `OutboxDeliveryMessage.cs` | Арендованная строка: полезная нагрузка плюс информация о доставке |
| `OutboxTaskDeliveryInfo.cs` | Информация о доставке на уровне задачи (`TaskId`, `DeliveryId`, `Attempt`, `LastErrorId`, `Status`) |
| `IConsumerGroupNamingStrategy.cs` | Стратегия санитизации имени группы потребителей |
| `Setup.cs` | `AddOutboxDelivery` — DI-развёртывание всего перечисленного |

## Конвейер доставки

1. **`DeliveryJob<TMessage>.Execute`** находит `OutboxConsumerSettings` в `IOutboxConsumerManager` по имени задачи и вызывает `IDeliveryProcessor.ProcessMessages`. Отсутствие регистрации — жёсткая ошибка, а не молчаливый no-op.
2. **`DeliveryProcessor.ProcessMessages`** выполняет жадный цикл. Каждая итерация: при необходимости пауза `IterationDelay`, обработка всех тенантов, затем `ShouldContinueProcessing`. Если группа на паузе, цикл ждёт 5 с и возвращается, не опрашивая базу.
3. **`DeliveryTenant.ProcessInTenant`** строит фильтр, спрашивает у `IDeliveryBatcher` размер, арендует буфер из `MemoryPool`, вызывает `RentDelivery` (переводя строки в `Processing` под `FOR UPDATE SKIP LOCKED`), запускает `LockRenewer` с интервалом `LockRenewal`, доставляет, затем вызывает `ReturnDelivery`.
4. **`DeliveryCourier.Deliver`** вызывает потребителя, превращает брошенное исключение в `Warn` с backoff, затем выполняет `PostHandle` — см. ниже.

Тенанты обрабатываются последовательно при `PerTenantMaxDegreeOfParallelism == 1` (значение по умолчанию) и через `Parallel.ForEachAsync` иначе; `-1` означает «по одному на ядро». Каждый проход по тенанту получает собственный связанный `CancellationTokenSource` с `PerTenantTimeout`.

## Политика приёма по статусам

Статусы образуют HTTP-подобную шкалу, и любое решение принимается на **двух независимых осях**: может ли задача быть взята в аренду вообще и считается ли исход успешной доставкой.

### 1. Какие статусы вообще берутся в обработку

`LockAndSelectStatusCodes` в `SqlOutboxBuilder` перечисляет ровно пять:

| Код | Статус | Смысл |
|---|---|---|
| 0 | `Pending` | Свеже опубликованное сообщение |
| 100 | `Processing` | Истёкший лок — воркер упал, задача берётся заново |
| 103 | `Postpone` | Отложено потребителем |
| 104 | `Retry` | Явный ретрай, запрошенный потребителем |
| 400 | `Warn` | Упало, ждёт окончания своего backoff |

Всё, что ≥ 500, терминально. Терминален и весь диапазон 2xx: приняв сообщение, потребитель выводит его из игры. Поверх статуса задача арендуется только при `task_lock_expires_on < @to` (лок истёк) и `msg_created_at >= @frm` (попадает в lookback-окно).

### 2. Кто и что ставит

| Ситуация | Статус | Кто ставит |
|---|---|---|
| Потребитель не тронул сообщение | `Ok` (200) | `PostHandle` — неявный успех |
| Потребитель вызвал `Ok` / `Created` / `Accepted` / `Ok203` / `NoContent` / `Aborted` | 200–299 | потребитель |
| Потребитель бросил исключение | `Warn` (400) + backoff | `HandleError` |
| Потребитель вызвал `Warn(ex, message, postpone)` | 400 + своя задержка | потребитель |
| `Attempt + 1 > MaxDeliveryAttempts` при статусе `Warn` | `MaximumAttemptsError` (508) | `PostHandle` |
| Потребитель вызвал `Error` / `Error501`…`Error507` | 500–507 | потребитель |
| Потребитель вызвал `Postpone(delay)` / `Retry(delay)` | 103 / 104 + задержка | потребитель |

### 3. Правила `PostHandle`

Порядок здесь важен и является несущей конструкцией:

1. **`IsAttemptsError` → 508, проверяется первым.** Требует состояния-предупреждения (400–499) *и* `Attempt + 1 > MaxDeliveryAttempts`. Эта проверка обязана выполняться **до** шорт-циркута «уже обработано», иначе статус 400 никогда не дошёл бы до DLQ и сообщение ретраилось бы вечно.
2. **Статус не `Pending` → не трогаем.** Решение потребителя окончательно.
3. **Остался `Pending` → `Ok()`.** Потребитель не задал статус и ничего не бросил — доставка успешна неявно.

`IsAttemptsError` — это `Code.IsWarning() && DeliveryInfo.Attempt + 1 > maxDeliveryAttempts`. Причина `+ 1` в том, что счётчик попыток в базе ещё не инкрементирован для обрабатываемого сбоя.

### 4. Что стоит помнить

- **`MovedPermanently` (301) — не успех.** Он находится вне `IsSuccess()` (200–299), но при этом вне `IsWarning()` и `IsError()`. Поэтому он терминален, не ретраится и **не** пишется в лог ошибок. Выбор этого статуса — осознанное утверждение, что сообщению нужен другой потребитель. При этом в счётчик обработанных он входит.
- **`Postpone` (103) намеренно не тратит попытку.** `SqlFinishDelivery` инкрементит `delivery_attempt` через `CASE WHEN 103 <> code THEN 1 ELSE 0 END`. Это сделано осознанно: отложкой полностью управляет потребитель, который сам решает, когда и нужно ли сдаваться. Поэтому `MaxDeliveryAttempts` относится к сбоям (`Warn`), а не к отложкам. Всё остальное, включая отложку, выраженную через `Warn`, попытку тратит. Если потребитель откладывает бесконечно, решение о попадании в DLQ остаётся за ним: он обязан в итоге вызвать `Error`/`Error5xx` (терминальный статус) или начать падать, и тогда в дело вступит `MaxDeliveryAttempts`.
- **Лок равен `result.CreatedAt + PostponeDelay`.** При успехе `PostponeDelay` равен нулю, но статус уже не берётся в аренду, так что это неважно. Для `Warn` с backoff лок держится ровно на длину backoff — именно это не даёт второму воркеру схватить ту же задачу.
- **В лог ошибок попадает только 5xx.** `GetErrors` фильтрует по `Exception != null && DeliveryResult.Code.IsError()`, поэтому transient-`Warn` больше не пишет строку в `__error$` и не проставляет `task.error_id`.
- **`Warn` без явной задержки ретраится не после backoff, а после batching window.** `PostponeDelay` по умолчанию равен нулю, поэтому потребитель, вызвавший `Warn(ex)` и забывший про задержку, вообще не получает экспоненциальный откат — задача просто снова становится доступной, как только `ToDate = now - BatchingWindow` сдвинется за неё. Всегда передавайте задержку (или позвольте курьеру вычислить её, бросив исключение).
- **Критические исключения не проглатываются.** `Deliver` ловит с фильтром `when (!ex.IsCritical())`. OOM / StackOverflow пролетают до того, как выполнится `PostHandle`, лок остаётся до истечения своего TTL, и сообщение берётся заново. Повтор предпочтительнее, чем запись мусора из умирающего процесса.

### 5. Что сообщается наружу

Проход по тенанту возвращает обычный `int` — количество обработанных сообщений. Парного «успешных» намеренно нет: именно этим счётчиком управляется `ShouldContinueProcessing`, а батч, в котором все сообщения ушли в `Warn` или в DLQ, — это всё равно разобранная работа. Счётчик успехов сделал бы полностью проваленный батч неотличимым от пустой очереди и остановил цикл. `ProcessMessages` возвращает накопленный счётчик, то есть сообщает объём обработанного, а не число успешных доставок.

Один счётчик вместо двух — тоже намеренно: «успех» в 200–299 ничего не решал вверх по стеку. Раньше он попадал в `Succeeded`, но на решение о продолжении цикла не влиял, а наверх уезжал как «доставлено», хотя батч из сплошных `Warn` отчитывался нулём.

## Настройки потребителя

`OutboxConsumerSettings` — неизменяемый `record`; `OutboxConsumerManager` хранит и подменяет целые снимки, поэтому настройка не может измениться посреди доставки.

| Настройка | По умолчанию | Билдер | Примечания |
|---|---|---|---|
| `ConsumerGroupId` | — | `WithConsumerGroupId` | Обязателен; также служит ключом DI для потребителя |
| `AsSingleton` | `true` | `AsSingleton` | `true` — один экземпляр на весь кластер, кешируется по id группы; `false` — новый DI-scope на батч |
| `Interval` | 1 мин | `WithInterval` | Периодичность задачи |
| `InitialDelay` | 10 с | `WithInitialDelay` / `StartImmediately` | `AddDeliveryJob` ставит ноль |
| `ConcurrencyLimit` | 1 | `WithConcurrencyLimit` | Параллелизм расписания на уровне группы |
| `MaxConcurrency` | 48 | `WithMaxConcurrency` | `AddDeliveryJob` переопределяет в 1 |
| `RetryCountOnError` | 0 | `WithRetryCountOnError` / `WithNoRetries` / `WithInfiniteRetries` | Повторы **задачи**, а не сообщения |
| `MaxBatchSize` | 16 | `WithMaxBatchSize` | Верхняя граница для `IDeliveryBatcher` |
| `MaxProcessingIterations` | 10 | `WithMaxProcessingIterations` / `WithSingleIteration` / `WithUnlimitedIterations` | `-1` — жадно, пока очередь не опустеет; `AddDeliveryJob` ставит `-1` |
| `IterationDelay` | ноль | `WithIterationDelay` | Вместе с нулём и `-1` цикл превращается в горячий слив |
| `LockDuration` | 10 с | `WithLockDuration` / `WithNoLockDuration` | Должен быть `> 0` |
| `LockRenewal` | 3 с | `WithLockRenewal` | Должно выполняться `0 < LockRenewal < LockDuration` |
| `LookbackInterval` | 7 дней | `WithLookbackInterval` | Нижняя граница окна выборки |
| `MaxDeliveryAttempts` | 3 | `WithMaxDeliveryAttempts` | Исчерпание превращает `Warn` в 508 |
| `BatchingWindow` | 3 с | `WithBatchingWindow` / `WithNoBatchingWindow` | `ToDate = now - BatchingWindow` — компромисс между задержкой и батчингом |
| `PerTenantTimeout` | ноль | `WithPerTenantTimeout` | Ноль = без таймаута |
| `PerTenantMaxDegreeOfParallelism` | 1 | `WithPerTenantMaxDegreeOfParallelism` / `WithTenantSequentialProcessing` / `WithTenantMaxParallelism` | `1` — последовательно, `-1` — по ядрам; `0` отклоняется |
| `Paused` | `false` | `Paused` / `Resumed` | Проверяется на каждой итерации цикла |

Обратите внимание: `AddDeliveryJob` заранее заполняет билдер значениями `StartImmediately()`, `ConcurrencyLimit(1)`, `MaxConcurrency(1)`, `MaxProcessingIterations(-1)` и `PerTenantTimeout(Zero)` **до** вызова вашего `configure`, поэтому эти значения выигрывают у `OutboxDefaults`.

### Инварианты блокировки

```csharp
LockDuration  > TimeSpan.Zero
LockRenewal   > TimeSpan.Zero  &&  LockRenewal < LockDuration
```

Оба проверяются в `OutboxConsumerSettings.Validate()`, который вызывается в `TryRegister` и при каждом `Apply`. Нулевой `LockDuration` оставил бы задачу в `Processing` навсегда после падения воркера; нулевой `LockRenewal` приводит к исключению внутри `PeriodicTimer`. `WithNoLockDuration()` выставляет TTL 50 мс (`OutboxConsumerSettingsBuilder.NoLockDuration`) — фактически без блокировки, ценой возможной повторной доставки.

## Управление во время выполнения

`IOutboxConsumerManager` — реестр, из которого читает задача доставки, и одновременно ручка операционного управления:

| Член | Поведение |
|---|---|
| `TryRegister` | Проверяет настройки, добавляет их, если группа неизвестна, возвращает `false`, если группа уже есть. Подписчиков уведомляет **вне** лока |
| `Apply` | Выполняет преобразование под локом, проверяет результат, сохраняет новый снимок. Бросает, если группа не зарегистрирована |
| `Get` / `IsRegistered` / `IsPaused` | Чтение под локом |
| `Pause` / `Resume` | `Apply(group, s => s with { Paused = ... })` — вступает в силу на следующей итерации цикла |
| `Unregister` | Удаляет настройки и подписчиков |
| `GetAllConsumerGroupIds` | Возвращает **копию**, а не живое представление — лок отпускается до того, как вызывающий перечислит коллекцию, поэтому выдача живого набора ключей открыла бы конкурентные мутации и привела бы к исключению |
| `Subscribe` | Возвращает дескриптор `IDisposable`; исключения подписчиков проглатываются и пишутся в `Debug`, чтобы один плохой подписчик не сломал доставку |

Поскольку настройки подменяются атомарно, изменение во время работы видно на следующей итерации и никогда посреди батча.

## DI-развёртывание

`Setup.AddOutboxDelivery` регистрирует через `TryAdd*` — любой из этих элементов можно переопределить до вызова:

```
FilterFactory (singleton)
IOutboxContextFactory      → OutboxContextFactory
IDeliveryBatcher           → DeliveryBatcher
IDeliveryProcessor         → DeliveryProcessor
IDeliveryCourier           → DeliveryCourier
IDeliveryTenant            → DeliveryTenant
IDeliveryLifetimeInvoker   → DeliveryLifetimeInvoker
IDeliverySnapshot          → DeliverySnapshot
IOutboxConsumerManager     → OutboxConsumerManager
```

Дополнительно вызываются `AddOutboxPartitional()` (поддержка тенантов) и `AddMessagesMetadata()`, затем выполняется ваш колбэк `IDeliveryBuilder`.
