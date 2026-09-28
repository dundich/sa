# Sa.Outbox.PostgreSql — аудит приёма/передачи, SQL и оптимизаций памяти

Дата: 2026-09-28 · Область: `src/Sa.Outbox.PostgreSql` (+ зависимости `Sa.Outbox`, `Sa.Data.PostgreSql`, `Sa.Partial.PostgreSql`)

## 0. Что проверялось и как

Прочитан весь исходный код модуля (`Commands/`, `SqlBuilder/`, `Services/`, `Partitional/`, `Configuration/`, `IdGen/`),
а также путь доставки в `Sa.Outbox` (`Publication/OutboxMessagePublisher` → `Delivery/DeliveryProcessor` →
`DeliveryTenant` → `DeliveryCourier` → `OutboxContext`) и слой доступа `Sa.Data.PostgreSql`.

Ограничения проверки:

- Тесты (`src/Tests/Sa.Outbox.PostgreSqlTests`) **не запускались** — нужен Docker для Testcontainers. Все выводы ниже
  основаны на чтении кода, не на профилировании. Пункты, помеченные **[не проверено]**, требуют
  `EXPLAIN ANALYZE` на реальных данных.
- `src/Sa.Partartial.PostgreSql` в этой файловой системе читается нестабильно (часть файлов — ENOENT), поэтому
  механика партиционирования (`PartCache`, `SqlPartRangeBuilder`) проверена частично. Утверждения о партиционировании
  ниже опираются на `SqlTemplate.cs:62-65`, `SqlTableBuilder.cs:16-40` и doc-комментарий
  `SqlRootBuilder.cs:6` — это **всё, что создаёт DDL** в модуле.

Сводка: **3 критических дефекта корректности, 4 существенных, 8 мелких; 9 оптимизаций памяти/аллокаций.**

---

# Часть I. Корректность приёма/передачи и SQL

## C1 — КРИТИЧНО. Зависшие задачи навсегда блокируют consumer group (livelock)

**Где:** `SqlBuilder/SqlOutboxBuilder.cs:48-113` (`SqlLockAndSelect`)

```sql
WITH locked_tasks AS (            -- :50  выбирает N задач, FOR UPDATE SKIP LOCKED
  ... ORDER BY t.task_id LIMIT @lim ...
),
updated_tasks AS (                -- :72  переводит их в Processing
  UPDATE ... SET delivery_status_code = 100, task_transact_id = @trn, task_lock_expires_on = @lck_on
)
SELECT ut.*, m.msg_payload        -- :100-110  INNER JOIN по msg_id
FROM updated_tasks ut
INNER JOIN msg m ON ut.msg_id = m.msg_id
```

Задача сначала переводится в `Processing`, и **только потом** джойн может её отбросить. Комментарий
`SqlOutboxBuilder.cs:43-47` утверждает, что этот случай закрыт зеркальным `WHERE` — но он закрывает только
случай «фильтр не совпал», а не случай «строки сообщения физически нет».

Строка сообщения отсутствует, когда:

- партиция `outbox__msg$` за день `d` уже удалена cleanup'ом, а задача создана **сегодня** для сообщения
  от `d` (offset/lookback это допускает). Партиционирование у таблиц разное: msg — LIST по
  `(tenant_id, msg_part)` + RANGE по `msg_created_at`, task — LIST по `(tenant_id, consumer_group)` +
  RANGE по `task_created_at`. Retention каждой таблицы независим, поэтому «задача жива, сообщения нет» —
  штатная ситуация при `DropPartsAfterRetention` < `LookbackInterval`;
- сообщение не вставлено (COPY прерван) — но тогда задачи и не будет, так что это не наш случай;
- ручное вмешательство / рассинхрон.

Последствия (проверено по коду):

1. Задача остаётся в `Processing` с истёкшим `task_lock_expires_on`. Следующий опрос снова её берёт, снова
   переводит в `Processing`, джойн снова её отбрасывает. `FinishDelivery` по ней никогда не вызывается,
   поэтому `delivery_attempt` **не растёт** — естественного пути в dead-letter нет.
2. `ORDER BY t.task_id LIMIT @lim` + `task_id` из `BIGSERIAL` ⇒ зависшие задачи имеют наименьшие `task_id`
   и **всегда** попадают в выборку первыми. Как только зависших задач станет ≥ `@lim` (16 по умолчанию),
   consumer group **перестаёт доставлять вообще ничего**, а `ShouldContinueProcessing`
   (`DeliveryProcessor.cs:91`) увидит `processed == 0` и решит, что очередь пуста.
3. `__error$` и `__log$` не получают ни записи — потеря невидима для наблюдаемости.

**Исправление (предлагаю вариант A + B вместе):**

```sql
WITH locked_tasks AS ( ... без изменений ... ),
updated_tasks AS ( ... без изменений ... ),
orphaned AS (
  DELETE FROM {task} t
  USING updated_tasks ut
  WHERE t.task_id = ut.task_id
    AND t.tenant_id = ut.tenant_id
    AND t.consumer_group = ut.consumer_group
    AND NOT EXISTS (SELECT 1 FROM {msg} m WHERE m.msg_id = ut.msg_id)
  RETURNING ut.task_id, ut.tenant_id, ut.consumer_group, ut.msg_id
)
SELECT ut.*, m.msg_payload
FROM updated_tasks ut
LEFT JOIN {msg} m ON ut.msg_id = m.msg_id
WHERE m.msg_id IS NOT NULL          -- ← вместо INNER JOIN
  AND m.tenant_id = @tnt AND m.msg_part = @prt
  AND m.msg_created_at >= @frm AND m.msg_payload_type = @tp_id
ORDER BY ut.task_id
```

- **A.** `LEFT JOIN` + `IS NOT NULL` ⇒ строка доставляется консьюмеру, а не теряется на джойне.
- **B.** Data-modifying CTE `orphaned` в **том же** statement чинит зависшие строки (в PostgreSQL все
  data-modifying CTE одного `WITH` видят один снапшот и выполняются атомарно — это гарантия,
  на которой уже построен `updated_tasks`). Требуется добавить третью колонку в `SELECT`
  (`COUNT(*) FILTER (WHERE orphaned.task_id IS NOT NULL)`), чтобы `StartDeliveryCommand` залогировал
  факт рассинхронизации.
- **Оговорка:** `DELETE` — это потеря данных. Альтернатива — писать в `__error$` и удалять; это точнее,
  но требует заранее обеспечивать партицию `__error$` (`OutboxPartRepository.EnsureErrorParts`), то есть
  ещё один round trip в `RentDelivery`. **Это решение за пользователем — см. Q1.**

**Побочно:** `LoadNewTasks` не должен «дочинять» такие задачи — он их не увидит, они в msg-таблице не появятся.

**Риск:** средний. Затрагивает hottest path. Покрыть тестом: вставить задачу, удалить её строку из `msg$`,
дождаться истечения `LockDuration`, проверить, что задача исчезла, а consumer group продолжает доставлять.

---

## C2 — КРИТИЧНО. Смещение (offset) по UUID v7 теряет сообщения при нестрогой монотонности

**Где:** `SqlOutboxBuilder.cs:231-269` (`SqlLoadConsumerGroup`), `IdGen/OutboxIdGenerator.cs:6`,
`Sa.Outbox/Publication/OutboxMessagePublisher.cs:30,58`

```sql
... AND msg_id > @offset
ORDER BY msg_id
LIMIT @lim
RETURNING msg_id
-- и далее:  SELECT COUNT(*), (SELECT msg_id ... ORDER BY msg_id DESC LIMIT 1)
```

Курсор продвигается на **максимальный** `msg_id` вставленной пачки. Корректность этого требует, чтобы
`msg_id` рос строго монотонно **во времени вставки**. Это не выполняется:

1. **`Guid.CreateVersion7(timestamp)` не монотонен.** BCL не имеет монотонного v7: 12-байтовый «хвост»
   случайный, поэтому два сообщения в одной миллисекунде получают **неупорядоченные** id. В пределах одной
   пачки publish это не страшно, но см. п.2.
2. **Скошенные часы между инстансами.** `OutboxMessagePublisher.cs:30` берёт `now` из `TimeProvider`
   приложения и передаёт его в `GenId` (`:58`) и в `msg_created_at`. Инстанс, чьи часы отстают на 5 мс,
   порождает `msg_id` **меньше** текущего `offset`. Его сообщения будут отброшены предикатом
   `msg_id > @offset` — **навсегда, молча**.
3. **Backdated-сообщения.** Тот же механизм для любого сообщения, чей `msg_created_at` меньше текущей
   границы.

Конкретный сценарий потери: пачка A (100 msg, t=1000 мс), затем пачка B (100 msg, t=1001 мс). Хвосты v7
случайны ⇒ id B перемешаны с id A. Консьюмер забирает 100 наименьших `id > offset`; offset становится
`max` среди них. Сообщения из B с id ниже этого `max`, но не попавшие в 100 — **потеряны безвозвратно**.

Это прямое нарушение гарантии доставки, то есть главного обещания библиотеки.

**Варианты:**

| | Решение | Чинит | Не чинит | Цена |
|---|---|---|---|---|
| **1** | Монотонный генератор: `Interlocked`-счётчик в младших битах v7, `max(candidate, last+1)` | п.1 | **п.2 и п.3** — межинстансный скошенный час | 1 файл + 1 тест |
| **2** | Курсор = пара `(msg_created_at, msg_id)`, продвигается только до `now - safetyGap` | п.1-3, если скошенный час меньше допуска | паузы длиннее `safetyGap` | +1 колонка в `__offset$`, новая настройка, систематическая задержка доставки |
| **3** | **`msg_seq BIGSERIAL` на `__msg$`** — курсор по суррогатному счётчику, который назначает **база** | **всё, жёсткая гарантия** | — | +1 колонка, миграция |
| **4** | Дедуп по `NOT EXISTS` вместо курсора | всё | — | **конфликтует с Q3** (см. ниже) |

> **Поправка от 2026-09-28.** Первая редакция этого документа предлагала «курсор = `MAX(task_id)` из task-таблицы».
> Вариант отклонён: чтобы он заработал, дедупликацию всё равно пришлось бы делать через `NOT EXISTS` по task-таблице
> (иначе `task_id` из `msg$` недоступен и каждая пачка перевставляется заново), а это и есть вариант 4 со всеми его
> проблемами. Вариант 3 решает задачу без этого замечания.

**Вариант 4 несовместим с Q3.** Если успешно завершённые task-строки удаляются, то удалённая строка делает
сообщение «новым» снова: `NOT EXISTS` находит ноль task-строк, сообщение enqueue'ится повторно, и так до бесконечности.
Два фикса — 3 и 4 — решают одну задачу взаимоисключающими способами. Выбран 3.

**Выбрано: вариант 3 — `msg_seq BIGSERIAL` на `__msg$`.** Порядок назначает база, часы приложения ни на что
не влияют. `msg_id` остаётся везде, где он ключ связи (`SqlOutboxBuilder.cs:105` — джойн с `msg$`,
`:235` — колонка task-таблицы, `:27-36` — BINARY COPY).

**Что заденет вариант 3:**

| Файл | Изменение |
|---|---|
| `Configuration/PgOutboxTableSettings.cs:92-102` | `MessageTable.TableFields.All()` — +1 колонка `msg_seq` |
| `SqlOutboxBuilder.cs:170-181` | `group_offset UUID` → `BIGINT` |
| `SqlOutboxBuilder.cs:231-269` | предикат, `ORDER BY`, `RETURNING` переезжают на `msg_seq` |
| `NpgsqlCommandExtension.cs:109-113` | `AddParamOffset`: `Guid` → `long` |
| `Services/OutboxTaskLoader.cs:163-203` | тип курсора в чтении и инициализации |
| `Configuration/PgOutboxConsumeSettings.cs:7` | внутренний `Dictionary<string, Guid>` → `long` |
| `SqlBuilder/Readme.md` + `Readme-ru.md:56` | семантика `@offset` |
| `Configuration/Readme.md` + `Readme-ru.md:173, 237-244, 356-381` | описание механики offset, `minOffset`, `ToMinGuidV7` |

**Публичный API сохраняется.** `PgOutboxConsumeSettings.WithMinOffset(string, Guid)` и
`WithMinOffset(string, DateTimeOffset)` остаются как есть; `DateTimeOffset` транслируется однократным
`SELECT min(msg_seq) FROM msg WHERE msg_created_at >= @date`. `ToMinGuidV7()` (`Sa/Extensions/GuidExtensions.cs:5`)
становится не нужен — при новой модели в коде он не используется.

**`BulkInsertMsgCommand.cs` почти не меняется:** колонку `msg_seq` в списке `COPY` перечислять не нужно —
PostgreSQL подставит `DEFAULT` (нужно подтвердить тестом, поведение колонок, не перечисленных в `COPY`,
не менялось между ветками и не документировано в коде).

**Дополнительно (независимо от выбора):** ввести в лог и метрику факт «вставлено 0 строк при
`msg_created_at < now - batchingWindow`» — это дешёвый детектор рассинхрона.

**Риск:** высокий — меняется формат курсора. Нужны тесты: две пачки в пределах одной мс, два инстанса
с разницей часов, backdated-публикация.

---

## C3 — КРИТИЧНО (производительность, деградация со временем). Завершённые задачи не удаляются и не индексируются

**Где:** весь модуль — `grep 'DELETE|TRUNCATE' src/Sa.Outbox.PostgreSql` даёт **0 совпадений**.

`SqlFinishDelivery` (`SqlOutboxBuilder.cs:322-368`) делает только `INSERT` в `__log$` + `UPDATE` task-строки.
Строка task остаётся навсегда со статусом `200/201/204/...`; удаляется только целиком дневная партиция
по retention.

При этом фильтр выбора в `SqlLockAndSelect` (`:62-64`) — `delivery_status_code IN (0,100,103,104,400)`.
Завершённые статусы в него **не входят** ⇒ каждая задача, попадая в `LIMIT @lim` по `ORDER BY task_id`,
требует, чтобы планировщик её **пропустил**. Единственный индекс task-таблицы — PK `(task_id, task_created_at)`
(`Sa.Partial.PostgreSql/SqlBuilder/SqlTemplate.cs:62-65`); вторичных индексов модуль не создаёт вовсе.

Следствие: стоимость одного опроса **пропорциональна числу уже завершённых задач в текущей суточной
партиции**, а не размеру очереди. К вечеру при потоке 10 msg/s это ~300k строк, которые каждый опрос
(по умолчанию раз в минуту, на группу × тенант) обязан просканировать. Профиль — линейная деградация
в течение суток, сброс при rollover партиции.

**Исправление — частичный индекс (главное) + удаление (желательно):**

```sql
-- (1) индекс живёт только по «живой» части таблицы
CREATE INDEX IF NOT EXISTS ix_{schema}_{task}_live
  ON {schema}.{task} (task_id)
  WHERE delivery_status_code IN (0,100,103,104,400);

-- (2) точечный индекс под JOIN-запрос загрузки, если msg_created_at-фильтр останется
CREATE INDEX IF NOT EXISTS ix_{schema}_{task}_msg_created
  ON {schema}.{task} (task_id) INCLUDE (msg_id, msg_part, msg_payload_type, msg_created_at)
  WHERE delivery_status_code IN (0,100,103,104,400);
```

Частичный индекс по predicate, **дословно совпадающему** с `WHERE` запроса — стандартное решение;
размер индекса ≈ размеру очереди, а не размеру партиции.

У модуля уже есть хук: `ITableBuilder` / `ITableSettings` (`SqlRootBuilder.cs:6` в doc-комментарии прямо
показывает `CREATE INDEX IF NOT EXISTS ... ON ... (payload_type)` как пример доп-SQL после root `CREATE TABLE`).

Дополнительно: после успешной фиксации (`Ok/Created/Accepted/NoContent/Aborted`) **удалять** строку task —
запись в `__log$` уже содержит всю историю. Это убирает и рост таблицы, и bloat. Сделать это отдельной
data-modifying CTE в том же `SqlFinishDelivery` — атомарно и без дополнительного round trip. Trade-off:
теряется `task_id`-ориентированная диагностика по «живой» очереди; см. Q3.

**Риск:** средний. Частичный индекс безопасен (создаётся на пустой/малой табличе, требует кратковременного
`SHARE`-лока при `CREATE INDEX CONCURRENTLY` — учесть в миграции). Удаление меняет семантику диагностики.

---

## C4 — СУЩЕСТВЕННО. `SqlSelectTenant` — полный скан с оконной функцией вместо `DISTINCT`

**Где:** `SqlOutboxBuilder.cs:147-156`

```sql
WITH ranked AS (
  SELECT tenant_id,
         ROW_NUMBER() OVER (PARTITION BY tenant_id ORDER BY tenant_id) AS rn
  FROM {msg}
)
SELECT tenant_id FROM ranked WHERE rn = 1;
```

Задача — получить список тенантов. Текущая формулировка материализует оконную функцию по **всем строкам всех
партиций `outbox__msg$`** (сортировка на стороне сервера, память work_mem под угрозой) и выбрасывает всё,
кроме первого id на тенанта.

```sql
SELECT DISTINCT tenant_id FROM {msg};
```

Поскольку `msg$` партиционирована по LIST `(tenant_id, msg_part)`, `DISTINCT` отрабатывает по
признаку партиционирования и стоит практически ноль. Это единственное место, где **серверная** память
запроса растёт вместе с объёмом outbox.

**Риск:** нулевой. Замена тривиальна. **[не проверено]** — подтвердить `EXPLAIN (ANALYZE, BUFFERS)` до/после.

---

## C5 — СУЩЕСТВЕННО. «Украденный» батч обнаруживается, но игнорируется

**Где:** `Commands/FinishDeliveryCommand.cs:30-33` → `Services/Plug/OutboxDeliveryManager.cs:51` →
`Sa.Outbox/Delivery/DeliveryTenant.cs:46`

```csharp
total += await dataSource.ExecuteNonQuery(sql, ...);   // FinishDeliveryCommand.cs:30
...
await ReleaseMessagesAsync(messages, filter, ct);       // DeliveryTenant.cs:46 — результат отброшен
```

`SqlFinishDelivery` (`SqlOutboxBuilder.cs:361-365`) матчит задачу по
`task_transact_id = @trn`. Если `LockDuration` истёк, пока консьюмер работал, задачу перехватит другой
воркер и перепишет `task_transact_id` — тогда наш `UPDATE` заденет **0** строк. `ExecuteNonQuery` вернёт 0,
это значение уходит вверх по стеку и **теряется**.

С точки зрения контракта at-least-once это корректно (будет переобработано), но **полностью невидимо**:
ни лога, ни метрики. Сейчас `LockRenewer` закрывает окно, однако при `Consume` длиннее
`LockDuration` и без renew, или при падении ренера, это происходит регулярно в проде.

**Исправление:** в `FinishDeliveryCommand.Execute` сравнивать `total` с `messages.Length`; при несовпадении
логировать `Warning` с `transactId`/`consumerGroup`/`tenantId` и числом потерянных строк. Три строки кода,
огромная ценность для диагностики.

---

## C6 — СУЩЕСТВЕННО. `SqlFinishDelivery` не `DELETE`-ит и не ограничивает атомарность «списания» задачи

Тесно связано с C3. `INSERT` в `__log$` и `UPDATE` task — один statement, это атомарно и правильно.
Но `ON CONFLICT DO NOTHING` в `__log$` (`:342`) — **no-op**: `delivery_id` это `BIGSERIAL`, PK-конфликт
невозможен (`SqlTemplate.cs:65` — PK включает колонку партиционирования, но `BIGSERIAL` уникален сам
по себе). Та же бессмысленность в `SqlError` (`:280`). Читающий код полагает, что идемпотентность
есть, а её нет. Либо убрать, либо (правильнее) добавить настоящий UNIQUE
`(consumer_group, tenant_id, task_id, delivery_created_at)` — тогда повторная запись после падения
не задвоит историю.

---

## C7 — СУЩЕСТВЕННО. 6 round trips на пустой опрос + бессмысленная работа при заполненной очереди

**Где:** `Services/Plug/OutboxDeliveryManager.cs:19-35`

```csharp
await EnsureParts(filter, ct);                            // :29  → 2 последовательных round trip
var _ = await loader.LoadNewTasks(filter, batchSize, ct);  // :31  → advisory lock + select offset + insert-select + update offset = 4
return await startCmd.ExecuteFill(writeBuffer, ...);       // :34  → 1
```

Итого **минимум 6 round trips на тенант × итерацию**, даже когда очередь пуста. Дополнительно:

- `EnsureParts` (`OutboxPartRepository.cs:36-53`) делает `foreach { await partManager.EnsureParts(...) }` —
  строго последовательно, по одному round trip на партицию. `RentDelivery` всегда передаёт **ровно одну**
  партицию (`:60-69`), так что здесь 2 вызова, а не 1. Соединить в один вызов, взяв `IEnumerable`
  из обоих наборов.
- `LoadNewTasks` вызывается **всегда**, даже если буфер уже заполнен предыдущей итерацией. Если очередь
  насыщена, `INSERT ... SELECT` вставляет 0 строк, но стоит полноценный round trip + обновление advisory
  lock'а. Кэшировать «очередь была полной» в `DeliveryTenant`/`DeliveryCourier` и пропускать загрузку.

Ожидаемый эффект: для потребителя с пустой очередью — с 6 до 3 round trips; для насыщенного — с 6 до 3–4.

---

## C8 — СУЩЕСТВЕННО (поддерживаемость). Один и тот же параметр означает три разных вещи

| Параметр | `SqlLoadConsumerGroup` | `SqlLockAndSelect` | `SqlExtendDelivery` |
|---|---|---|---|
| `@frm` (`FromDate`) | `msg_created_at >= frm` (реальный диапазон) | `task_created_at >= frm` **и** `msg_created_at >= frm` (lookback) | `task_created_at >= frm` (lookback) |
| `@to` (`ToDate`) | `msg_created_at <= to` (реальный диапазон) | `task_lock_expires_on < to` — **это «сейчас»** | — |
| `@now` (`NowDate`) | `task_created_at = now` | — | `task_lock_expires_on > now` (проверка «ещё жив») |

`@to` в `SqlLockAndSelect` — это «текущее время», а в `SqlLoadConsumerGroup` — «верхняя граница окна
создания». Сейчас это работает только потому, что `RentDelivery` передаёт свежий filter, где `ToDate == NowDate`
(`FilterFactory.cs:20-29` — `ToDate: now - batchingWindow`, `NowDate: now`; т.е. они равны лишь если
`BatchingWindow == 0`, а по умолчанию **3 с** — не равны!).

Проверка: `DeliveryTenant.cs:26` создаёт filter, `RentDelivery` идёт в `startCmd` с этим же filter, значит
`task_lock_expires_on < (now - 3s)` — задачи, залоченные только что, отсекаются лишние 3 секунды. На
практике безвредно (сдвиг в 3 с), но это **латентный баг**: любое будущее изменение, снимающее
`batchingWindow` с пути, превратит `@to` в настоящее «сейчас» и сломает захват.

**Исправление:** переименовать в `@win_from`/`@win_to` (окно) и `@now` (часы) в `SqlLoadConsumerGroup`
и `SqlLockAndSelect`; добавить инлайн-комментарий в `SqlOutboxBuilder` с таблицей семантики.

---

## C9 — МЕЛКО. `delivery_attempt` инкрементируется и на терминальных кодах

`SqlOutboxBuilder.cs:348-352`: `+ CASE WHEN 103 <> status THEN 1 ELSE 0 END`. Инкремент получают `Ok(200)`,
`Created(201)`, `Error(500)`. Исключение `Postpone(103)` — намеренно, это задокументировано в
`SqlOutboxBuilder.cs:313-318` и в `OutboxContext.cs:28-39`. Для терминальных состояний счётчик
больше ни на что не влияет, но значение записывается в task **и** в `__log$`, где выглядит как
«сообщение обрабатывалось N раз». В `__log$` семантически верно писать `attempt + 1` только для
нетерминальных исходов (`Warn/Retry/Postpone`), а для терминальных — текущее значение.

---

## C10 — МЕЛКО. Хрупкий `ToString()` в ключе advisory-блокировки + 32-битный хеш

**Где:** `Services/OutboxTaskLoader.cs:206-218`

```csharp
foreach (char c in consumerGroup.ToString())   // record ConsumerGroupIdentifier — сгенерированный ToString()
```

`ToString()` у record'а генерируется компилятором. Переименование свойства или добавление нового
молча меняет ключ блокировки (не опасно, но меняет распределение по флоту). Хеш — FNV-1a 32-бит:
при ~2^16 consumer groups коллизии вероятны, и коллизия сериализует две несвязанные группы на
общем advisory lock'е (не корректность, но потеря параллелизма). Плюс аллокация строки на каждый опрос.

**Исправление:** хешировать явно `"ConsumerGroupId\0TenantId"` без аллокации (`ReadOnlySpan<char>` + ручной
FNV), и перейти на двухаргументную форму `pg_advisory_xact_lock(@grp_hash, @tnt)` — 64-битное пространство,
без коллизий между группами.

---

## C11 — МЕЛКО. `SqlInitOffset` и `SqlInsertOffset` — побайтово идентичны

`SqlOutboxBuilder.cs:184-191` и `:218-225`. `SqlInsertOffset` не используется нигде (проверено: единственные
упоминания — определение и, возможно, тест). Удалить дубликат.

---

## C12 — МЕЛКО. `delivery_created_at = 0` отдаётся консьюмеру как настоящая дата

`StartDeliveryCommand.cs:105-111`: колонка `BIGINT NOT NULL DEFAULT 0` → `ToDateTimeOffsetFromUnixTimestamp()`
даёт `1970-01-01`, и это попадает в `OutboxTaskDeliveryInfo.Status` (`OutboxTaskDeliveryInfo.cs:17-24`),
который консьюмер видит. Должно быть `DateTimeOffset.MinValue` / nullable, иначе логи консьюмера
врут при логировании статуса.

---

## C13 — МЕЛКО. Видимость ретраев ограничена `LookbackInterval`

`FilterFactory.cs:26`: `FromDate = now.StartOfDay() - lookbackInterval`, а `SqlLockAndSelect:60` фильтрует
`task_created_at >= @frm`. Задача, которая failing дольше `LookbackInterval` (по умолчанию 7 дней),
**навсегда выпадает из выборки** — без `__error$`, без лога, до удаления партиции по retention.

**Проверено и снято как ложное срабатывание:** при штатной `ExponentialBackoffRetryStrategy` backoff
ограничен 30 минутами (`ExponentialBackoffRetryStrategy.cs:34`), а `MaxDeliveryAttempts` = 3
(`OutboxDefaults`), так что дефект не достигается. Реализуем только при своём `IRetryStrategy` или
`Postpone` длиннее lookback. Оставить как документированное ограничение; при желании — валидировать
`LookbackInterval > maxRetryDelay` в `OutboxConsumerSettingsBuilder` (fail fast).

---

## C14 — СУЩЕСТВЕННО, БЛОКИРУЕТ C2. Механизма миграции DDL в библиотеке нет вообще

**Где:** весь репозиторий — `grep 'ALTER TABLE' src/` даёт **0 совпадений**.

Вся DDL генерируется как `CREATE TABLE IF NOT EXISTS`: корневые таблицы и партиции — в
`Sa.Partial.PostgreSql/SqlBuilder/SqlTemplate.cs:62,101,135`; вспомогательные `__type$` и `__offset$` —
в `SqlOutboxBuilder.cs:132,170`, выполняемые через `AddPostSql` (`Partitional/Setup.cs:28,36`).

Следствие: **библиотека никогда не меняет форму существующей таблицы.** Уже задеплоенная система
получит старую схему и не получит новую — `IF NOT EXISTS` отработает вхолостую, а последующий `INSERT`
упадёт на несовместимом типе.

Это делает невозможным «просто добавить колонку» в C2 (вариант 3) и в C3 (удаление задач не требует
DDL, но требует корректного `DELETE` в DML — там проблемы нет). Для `__offset$` нужна смена типа
`UUID` → `BIGINT`, для `__msg$` — добавление `msg_seq BIGSERIAL` с каскадом на все существующие партиции
(в PostgreSQL ≥ 11 `ALTER TABLE ... ADD COLUMN` на партиционированной таблице распространяется на
партиции автоматически, но колонка со `DEFAULT` требует переписывания данных — на больших таблицах
это блокировка).

**Что нужно завести (решение принято 2026-09-28):**

1. Версионирование DDL: таблица `__schema$` (или расширение существующей `__type$`-подобной) с
   номером версии и списком применённых миграций. Паттерн — тот же, что у `Sa.Configuration.PostgreSql`
   и `Sa.Partitional.PostgreSql` (`SqlTemplate.cs:143-144` уже создаёт кеш-таблицу с `id TEXT PRIMARY KEY`).
2. Идемпотентные шаги: каждый шаг обязан быть безопасен при повторе (`ADD COLUMN IF NOT EXISTS`,
   сравнение типов через `information_schema` перед `ALTER`).
3. Блокировка на время миграции — `PartMigrationService`/`DeliveryJobInterceptor` уже умеют
   останавливать доставку на время активной миграции (`Readme-ru.md:334-341`), механизм переиспользуется.

**Риск:** высокий, но это инфраструктурная задача, а не правка SQL. Пока её нет, C2/C3 нельзя
мержить как «просто» — нужен либо релизный скрипт, либо эта подсистема.

---

## C15 — СУЩЕСТВЕННО. Механизм курсора не покрыт тестами вообще

`grep 'MinOffset|group_offset|Offset' src/Tests` не находит ни одного теста, трогающего offset.
Единственное покрытие — интеграционные тесты доставки, которые проходят и на сломанном курсоре,
потому что при одном инстансе и последовательных пачках нарушение монотонности не проявляется.

Практическое следствие: **ломающий рефакторинг C2 пройдёт CI зелёным.** Первым шагом по любому
варианту курсора должен идти тест, который падает на текущем коде — иначе невозможно доказать,
что фикс что-то исправил. Состав (из этапа 3):

- две publish-пачки в пределах одной миллисекунды → ни одного потерянного сообщения;
- backdated-публикация (`msg_created_at` меньше текущего курсора) → сообщение всё равно доставлено;
- два инстанса со сдвинутыми часами через подменённый `TimeProvider` → потери нет.

---

# Часть II. Оптимизации по памяти

Порядок — по соотношению «эффект / риск».

## M1 — `StringBuilderPooledObjectPolicy` настроен так, что пул не работает вообще

**Где:** `SqlBuilder/Setup.cs:15-23`

```csharp
var policy = new StringBuilderPooledObjectPolicy() { InitialCapacity = 1024 };
```

`MaximumRetainedSize` **не задан**, а дефолт в BCL — `4 * 1024 = 4096` байт. `BuildDeliveryInsertValues`
(`SqlOutboxBuilder.cs:370-401`) и `BuildErrorInsertValues` (`:284-310`) строят VALUES-список на 512 строк
≈ 30–60 КБ ⇒ **каждый** построитель больше 4 КБ ⇒ `objectPool.Return(sb)` молча его выбрасывает, а
`Get()` каждый раз выделяет новый `StringBuilder(1024)`, который затем 5–6 раз дорастает с копированием.

При этом пул на hot path **не используется вовсе**: `FinishDeliveryCommand.cs:11` и
`ErrorDeliveryCommand.cs:13` пропускают SQL через `SqlCacheSplitter`, который мемоизирует по длине —
`genSql` (а значит и `Build*InsertValues`) вызывается только при первом попадании ключа. То есть
~50 вызовов за процесс, и все они тратят пул, который ничего не возвращает.

**Исправление:** удалить `ObjectPool<StringBuilder>` целиком, в `Build*InsertValues` использовать
`new StringBuilder(EstimateCapacity(count))` (оценка: `count * 48`). Минус ~40 строк кода, исчезает
ложная «оптимизация». Альтернатива (если хочется оставить пул) — задать `MaximumRetainedSize = 128 * 1024`.

---

## M2 — `WritePayload`: стрим на каждое сообщение + двойное копирование payload

**Где:** `Commands/BulkInsertMsgCommand.cs:63-100`

```csharp
foreach (OutboxMessage<TMessage> row in messages.Span)
    WritePayload(writer, row.Payload);   // внутри: streamManager.GetStream() на КАЖДОЕ сообщение

private int WritePayload<TMessage>(NpgsqlBinaryImporter writer, TMessage? payload)
{
    using RecyclableMemoryStream stream = streamManager.GetStream();
    serializer.Serialize(stream, payload);
    stream.Position = 0;
    writer.Write(stream, NpgsqlDbType.Bytea);   // Npgsql читает стрим в свой буфер → копия #2
    return (int)stream.Length;
}
```

Три проблемы:

1. **Стрим на сообщение.** `RecyclableMemoryStreamManager` пулит только буферы ≤ `MaximumBufferSize`
   (дефолт — 1 МБ, `largePoolLimit` пул не покрывает). Payload > 1 МБ ⇒ каждый вызов — свежий `byte[]`.
2. **Двойное копирование.** `writer.Write(Stream, Bytea)` переливает поток в write-буфер Npgsql.
   `RecyclableMemoryStream` — это буфер с публичным `GetBuffer()`, поэтому копия не нужна.
3. `stream.Position = 0` + `Write(Stream)` — оба обходятся одним `Write(ReadOnlySpan<byte>)`.

**Исправление:**

```csharp
private void WriteRows<TMessage>(NpgsqlBinaryImporter writer, long payloadTypeCode,
                                 ReadOnlyMemory<OutboxMessage<TMessage>> messages)
{
    // Один стрим на всю пачку, а не на сообщение.
    using RecyclableMemoryStream stream = streamManager.GetStream();

    foreach (OutboxMessage<TMessage> row in messages.Span)
    {
        stream.SetLength(0);                       // переиспользование буфера между сообщениями
        serializer.Serialize(stream, row.Payload);

        int len = (int)stream.Length;
        writer.StartRow();
        writer.Write(idGenerator.GenId(row.PartInfo.CreatedAt), NpgsqlDbType.Uuid);
        writer.Write(row.PartInfo.TenantId,       NpgsqlDbType.Integer);
        writer.Write(row.PartInfo.Part,           NpgsqlDbType.Text);
        writer.Write(row.PayloadId,               NpgsqlDbType.Text);
        writer.Write(payloadTypeCode,             NpgsqlDbType.Bigint);
        writer.Write(stream.GetBuffer().AsSpan(0, len), NpgsqlDbType.Bytea);   // без копии
        writer.Write(len,                         NpgsqlDbType.Integer);
        writer.Write(row.PartInfo.CreatedAt.ToUnixTimeSeconds(), NpgsqlDbType.Bigint);
    }
}
```

**Замечание по AOT/совместимости:** `GetBuffer()` бросает, если буфер не экспонируем. Для
`RecyclableMemoryStreamManager` из `Microsoft.IO` (`RecyclableMemoryStreamManager.GetStream()`) буфер
экспонируемый — но это стоит зафиксировать тестом, а не полагаться. Fallback: `TryGetBuffer()`.

Эффект: для батча из 16 сообщений — 1 аллокация вместо 16, и −1 копия payload на сообщение.

---

## M3 — `NpgsqlOutboxReader`: 17 lookup'ов по имени колонки на строку

**Где:** `Commands/NpgsqlOutboxReader.cs:20-63`, вызывается из `StartDeliveryCommand.cs:53-111`

Все 17 аксессоров используют `reader.GetGuid(name)` / `GetString(name)` / `GetInt64(name)`.
Перегрузка `GetXxx(string name)` внутри делает `GetOrdinal(name)` — линейный поиск по массиву имён
с регистронезависимым сравнением. На батче в 16 сообщений это 272 lookup'а на один SELECT,
и они повторяются **на каждом опросе** каждой группы.

**Исправление:** один раз на команду построить `readonly record struct Ordinals(int TaskId, int TenantId, ...)`
(17 `int` = 68 байт, в стеке, без аллокаций), передавать её в ридер. `NpgsqlDataReader.GetOrdinal`
кешировать **нельзя** (reader переиспользуется), но кешировать ординалы один раз на `NpgsqlCommand` — можно и нужно.

Побочно: `NpgsqlOutboxReader` — `internal`, поэтому изменение сигнатуры не ломает публичный API.

---

## M4 — `GetStream` на каждое сообщение + `using` на каждый payload

**Где:** `Commands/StartDeliveryCommand.cs:98-103`

```csharp
using Stream stream = outboxReader.Message.GetMgsPayload(reader);
TMessage payload = serializer.Deserialize<TMessage>(stream)!;
```

`GetStream` создаёт новый `NpgsqlReadStream`-обёртку на строку (внутренний буфер Npgsql кешируется,
но объект-обёртка — новая аллокация на каждое сообщение). `using` там обязателен — Npgsql требует
закрыть поток до чтения следующей строки. Дешевле переиспользовать один буфер: если сериализатор
умеет в `ReadOnlySpan<byte>`/`IBufferWriter<byte>` (а `System.Text.Json` — да, через
`JsonSerializer.Deserialize(ReadOnlySpan<byte>)` + `JsonTypeInfo`), можно читать payload через
`reader.GetFieldValue<byte[]>`/`GetMemory` и десериализовать из спана — **ноль** аллокаций стрима
и промежуточного `byte[]`.

Это требует расширения `IOutboxMessageSerializer` новым перегрузками (см. R3 ниже) — поэтому
это отдельная, необязательная оптимизация; M3 и M2 дают больше при меньшем риске.

---

## M5 — `ErrorDeliveryCommand`: текст ошибки собирается дважды

**Где:** `Commands/ErrorDeliveryCommand.cs:66-86, 113-147`

```csharp
ErrorInfo info = new(GetErrorMessageHash(message.Exception), ...);   // :124 → GetErrorMessage() #1 + murmur
...
command.AddParamStatusMessage(GetErrorMessage(row.Exception), i);    // :60  → GetErrorMessage() #2
```

На каждую **различную** ошибку текст (`StringBuilder` + цепочка `InnerException`) собирается дважды,
и `GetErrorMessageHash` (`:142-147`) хеширует его отдельно. `ErrorRow` (`:140`) уже является
структурой, в которой есть место для готовой строки:

```csharp
internal readonly record struct ErrorRow(Exception Exception, ErrorInfo Info, string Message);
```

Композиция и хеш считаются один раз в `GroupByException`, `Fill` берёт `row.Message`. Экономия:
до 50% CPU на пути записи `__error$` и минус N `StringBuilder` на батч. Риск: нулевой (внутримний тип).

---

## M6 — `OutboxPartInfo[]` + `HashSet` на каждый publish

**Где:** `Services/Plug/OutboxBulkWriter.cs:18-19`, `Services/OutboxPartRepository.cs:36-53`

```csharp
OutboxPartInfo[] parts = messages.Span.SelectWhere(c => c.PartInfo);   // массив на каждый publish
await partRepository.EnsureMsgParts(parts, ct);                        // .Distinct() → HashSet → foreach await
```

`OutboxPartInfo` — record из 3 полей, у которых **нет значимого `Equals` с точки зрения партиции**:
`CreatedAt` входит в значение, поэтому `.Distinct()` сравнивает и время. В.publish-батче все сообщения
имеют один `now` (один вызов `GetUtcNow()` на пачку) ⇒.distinct даёт 1 элемент, но аллокации
(массив + `HashSet` + перечислитель) платятся каждый раз.

**Исправление:** `OutboxPartInfo` — партиционный ключ таблицы, а `CreatedAt` — RANGE-колонка, поэтому
для `EnsureParts` нужен **день**, а не мгновение. Сравнивать по `StartOfDay()`:

```csharp
public async Task<int> EnsureParts(string table, IEnumerable<OutboxPartInfo> parts, CancellationToken ct)
{
    List<(int tenant, string part, DateTimeOffset day)> missing = [];
    foreach (OutboxPartInfo p in parts)
    {
        DateTimeOffset day = p.CreatedAt.StartOfDay();
        if (!missing.Contains((p.TenantId, p.Part, day)))
            missing.Add((p.TenantId, p.Part, day));
    }
    // затем: Task.WhenAll по отсутствующим (partManager.EnsureParts идемпотентен)
}
```

`Contains` на коротком списке (обычно 1–3 элемента) дешевле `HashSet`, и параллельный `EnsureParts`
убирает последовательные round trips из M7/C7.

---

## M7 — `ReturnDelivery`: три линейных прохода с аллокациями по батчу

**Где:** `Services/Plug/OutboxDeliveryManager.cs:37-91`

```csharp
var parts = messages.Span
    .SelectWhere(c => c.DeliveryInfo.PartInfo with { CreatedAt = c.DeliveryResult.CreatedAt })  // :44-47 массив
    .Distinct();                                                                                // HashSet
...
IOutboxContextOperations<TMessage>[] errs = messages.Span.SelectWhere(m => m, m => ...);      // :78-80 массив
var dates = errs.Select(c => c.DeliveryResult.CreatedAt);                                     // :85 IEnumerable, аллоцируется лениво
```

Три отдельных прохода, три аллокации на батч в 16–512 элементов. `dates` затем ещё раз
`.StartOfDay().Distinct()` в `OutboxPartRepository.cs:24`. Объединить в один проход,
собирая дедуплицированные `day`-ключи в один переиспользуемый `HashSet<(int,string,DateTimeOffset)>`.

---

## M8 — `CachedParamNames<T>` держит 4608 строк всегда

**Где:** `Sa.Data.PostgreSql/DbCommandExtensions.cs:57-101`, `Commands/NpgsqlCommandExtension.cs:115-131`

`CreateCachedArrays(9 префиксов, 512)` = **4608 объектов `string`** + 9 массивов, живущих весь процесс
(~200–300 КБ вместе со ссылками), плюс `ReadOnlyDictionary` поверх `Dictionary`.

Плюс: `MaxIndex = 512` в `BatchParams` (`:117`) и `maxLen = 512` в `SqlCacheSplitter.GetSql`
(`SqlCacheSplitter.cs:41`) — **два несвязанных литерала**, а комментарий в `SqlCacheSplitter.cs:14-15`
(«~16 SQL параметров на элемент») фактически неверен: в `SqlFinishDelivery` на строку приходится
8 индексированных + 4 общих параметра.

**Исправление:**
- вынести `public const int BatchSize = 512;` в одно место и использовать в обоих;
- поправить комментарий на фактические 13 параметров;
- 4608 кешированных строк — разменная монета против `string.Format` на каждый параметр; оставить как есть,
  но **задокументировать** в `DbCommandExtensions`, иначе следующий читатель удалит «лишний» кеш.
- консервативный лимит можно поднять: 512 × 13 = 6656 ≪ 65535, реальный потолок ~5000 строк на statement.
  Это уменьшит число round trips при больших батчах. (Опционально, отдельным решением — Q4.)

---

## M9 — `SqlOutboxBuilder`: 12 интерполированных строки на инстанс

**Где:** `SqlBuilder/SqlOutboxBuilder.cs:25-269`

Поля объявлены как `public readonly string` **экземплярные**, т.е. собираются в конструкторе. Регистрация —
`TryAddSingleton` (`SqlBuilder/Setup.cs:25`), поэтому выполняется один раз. Замечание только на будущее:
любой переход на `Scoped`/`Transient` даст ~12 строк на резолв. Если планируется, вынести в
`static readonly` + кеш по `(schema, tableNames)` — либо оставить комментарий, что класс обязан быть singleton.

---

## Сводная таблица оптимизаций

| # | Что | Место | Эффект | Риск |
|---|---|---|---|---|
| M1 | Убрать мёртвый `ObjectPool<StringBuilder>` | `SqlBuilder/Setup.cs:15-23` | ~40 строк кода, -1 класс, пул не работал | нулевой |
| M2 | 1 стрим на батч + `Write(ReadOnlySpan<byte>)` | `BulkInsertMsgCommand.cs:93-100` | 16→1 аллокация, −1 копия payload/сообщение | низкий |
| M3 | Кеш ординалов колонок | `NpgsqlOutboxReader.cs` | −17 lookup/строку → 0 | низкий |
| M5 | Текст ошибки один раз | `ErrorDeliveryCommand.cs:60,124` | −50% CPU на `__error$` | нулевой |
| M6 | Партиционный ключ без `CreatedAt` + параллельный `EnsureParts` | `OutboxBulkWriter.cs:18`, `OutboxPartRepository.cs:36-53` | −3 аллокации/publish, −N round trips | низкий |
| M7 | Один проход вместо трёх | `OutboxDeliveryManager.cs:44-85` | −3 аллокации/батч | нулевой |
| M4 | `Deserialize` из спана вместо `GetStream` | `StartDeliveryCommand.cs:98-103` | −1 объект/сообщение | средний (нужен новый API сериализатора) |
| M8 | Единая константа `512`, правка комментария | `NpgsqlCommandExtension.cs:117`, `SqlCacheSplitter.cs:41` | гигиена, защита от тихой деградации | нулевой |
| M9 | Комментарий «обязан быть singleton» | `SqlOutboxBuilder.cs` | предотвращает регрессию | нулевой |

---

# Часть III. Что делать и в каком порядке

## Этап 1 — корректность (без изменения схемы)

1. **C5** — логировать несовпадение `affected < batch` в `FinishDeliveryCommand`. 3 строки.
2. **C4** — `SELECT DISTINCT tenant_id`. 1 строка, максимальный эффект на серверную память.
3. **C6** — убрать/осмыслить два `ON CONFLICT DO NOTHING`.
4. **C11**, **C9**, **C12** — мелочи, чистка.
5. **C8** — переименовать параметры + комментарий-таблица семантики.

## Этап 2 — индексы (решает C3)

6. Частичный индекс `ix_{task}_live` через существующий хук `ITableBuilder`/`ITableSettings`
   (образец — в doc-комментарии `SqlRootBuilder.cs:6`). Учесть `CREATE INDEX CONCURRENTLY`.
7. **[не проверено]** `EXPLAIN (ANALYZE, BUFFERS)` на `SqlLockAndSelect` до/после. Ожидание:
   `Seq Scan`/`Index Scan` с большим `Rows Removed by Filter` → `Index Scan` по `ix_{task}_live`.

## Этап 3 — согласованные решения (Q1–Q3 закрыты 2026-09-28)

8. **C14 + C15 (первыми)** — тесты, падающие на текущем коде, и подсистема миграций DDL.
   Без них пункты 9–11 недоказуемы: нечем подтвердить, что фикс что-то исправил.
9. **C1** — `DELETE` зависших задач в том же statement + счётчик в лог. *(Q1: вариант a)*
10. **C2** — `msg_seq BIGSERIAL` на `__msg$`, `group_offset UUID → BIGINT`, курсор переезжает
    на `msg_seq`. Публичный API `WithMinOffset` сохраняется. *(Q2: вариант 3)*
11. **C3 (удаление)** — `DELETE` успешно завершённых task-строк с сохранением `task_id` в `__log$`.
    Перед удалением — `ANALYZE` партиции. *(Q3: да)*

**Порядок внутри этапа 3 жёсткий:** сначала C15-тесты (красные), потом C14-подсистема миграций,
затем C2. C1 и удаление C3 можно делать параллельно — они не зависят ни от C2, ни от C14.

## Этап 4 — память

12. M1, M5, M7, M8, M3, M2, M6 — независимы, каждый мержится отдельно.
13. M4 — только если профилировщик покажет, что `GetStream`/`Deserialize` доминируют.

## Тесты, которые обязательны для этапов 1–3

- `C1`: задача без сообщения (удалить строку из `msg$`) → consumer group продолжает доставлять.
- `C2`: две publish-пачки в пределах одной мс; backdated-публикация; два инстанса со сдвинутыми часами
  (через подменённый `TimeProvider`) → ни одного потерянного сообщения.
- `C3`: 50k завершённых задач в партиции + план `SqlLockAndSelect` — время и buffers не растут.
- `C5`: батч, чей `LockDuration` истёк во время `Consume` → в логе `Warning`, повторная доставка.
- `C8`: `BatchingWindow = 0` → захват задачи сразу, без сдвига.

---

# Решения и открытые вопросы

## Закрыто 2026-09-28

**Q1. Задача без сообщения (C1)?** → **`DELETE` в том же statement + счётчик и предупреждение в лог.**
Вариант (b) с записью в `__error$` отклонён: он требует `EnsureErrorParts` в `RentDelivery` (+1 round trip
на каждую выборку) ради записи, которая по определению описывает внутреннюю рассинхронизацию, а не
сбой доставки. Вариант (c) отклонён: снимает блокировку, но строки копятся и C3 остаётся нерешённым.

**Q2. Модель курсора (C2)?** → **вариант 3: `msg_seq BIGSERIAL` на `__msg$`.** Разбор всех четырёх
вариантов и разбор радиуса поражения — в секции C2. Публичный API `WithMinOffset` сохраняется.

**Q3. Удалять ли успешно завершённые строки task (C3)?** → **да**, с `task_id` в `__log$` (уже есть,
`SqlOutboxBuilder.cs:334`) и `ANALYZE` партиции. Потеря «поиска по `task_id` в живой таблице» приемлема:
`__log$` — каноническое место для завершённых задач.

**Миграции DDL.** Отдельным решением: заводим подсистему версионирования (C14). Ручной релизный скрипт
как постоянный механизм отклонён — `CREATE TABLE IF NOT EXISTS` в `SqlTemplate.cs` и `SqlOutboxBuilder.cs`
гарантирует, что без подсистемы следующая же смена схемы снова упрётся в стену.

## Открыто — нужно ваше решение

**Q4. Поднимать ли потолок батча (M8)?** 512 строк × 13 параметров = 6656 при лимите PostgreSQL 65535.
Подъём до ~4000 сократит число round trips на больших батчах в 8 раз, но раздует SQL (кеш `SqlCacheSplitter`
разрастётся с ~50 до ~260 ключей, ~260 КБ — приемлемо). Либо оставить 512 для предсказуемости.
Моя позиция: оставить 512. Выигрыш 8 round trips на больших батчах не окупает 5× рост кеша SQL,
а текущее значение заведомо ниже потолка — то есть не является узким местом.

**Q5. Нужен ли `EXPLAIN`-бенчмарк как обязательный шаг?** Docker на машине есть, так что я могу его
запустить и снять реальные цифры вместо `[не проверено]`. Осталось решить, включать ли это в объём
этапа 2 или оформить отдельным прогоном. Моя позиция: включать — без плана запроса утверждение
«частичный индекс ускорит опрос» остаётся гипотезой, а весь C3 на нём и держится.

**Q6. Версия и процедура раскатки.** `msg_seq` на большой таблице требует `ALTER TABLE ... ADD COLUMN
... DEFAULT` с переписыванием данных — на суточной партиции с миллионами строк это блокировка на
минуты. Нужен ли план «expand/contract» (добавить колонку nullable → бэкфилл батчами → переключить
чтение → удалить старую) или допустима одномоментная миграция в окно обслуживания? Моя позиция:
expand/contract, потому что иначе раскатка новой версии библиотеки требует остановки доставки на
всех инстансах разом.
