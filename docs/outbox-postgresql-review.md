# Sa.Outbox.PostgreSql — аудит приёма/передачи, SQL и оптимизаций памяти

Дата: 2026-09-28 · Область: `src/Sa.Outbox.PostgreSql` (+ зависимости `Sa.Outbox`, `Sa.Data.PostgreSql`, `Sa.Partitional.PostgreSql`)

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

**Что заденет вариант 3** *(план на 2026-09-28; фактический состав реализации, включая expand по `__offset$` — ниже в «Реализовано 2026-09-29»)*:

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

### Реализовано 2026-09-29 (вариант 3, финальный состав)

Курсор потребления переехал с `msg_id` (UUID v7) на `msg_seq` (BIGINT, порядок вставки, назначаемый базой).

| Таблица | Колонка | Что сделано |
|---|---|---|
| `__msg$` | `msg_seq BIGSERIAL NOT NULL` | Порядок назначает база в момент COPY; `BulkInsertMsgCommand` **не** перечисляет колонку — подставляется `DEFAULT` (проверено пином `SqlBulkMsgCopy_DoesNotListMsgSeq`) |
| `outbox` (`__task$`) | `msg_seq BIGINT NOT NULL DEFAULT 0` | Денормализованный seq рядом с `msg_id` — чтобы `RETURNING msg_seq` из `SqlLoadConsumerGroup` мог вернуть курсор по вставленным строкам |
| `__offset$` | `group_offset UUID` (легаси) + `group_offset_seq BIGINT NOT NULL DEFAULT 0` (живой) | **Expand/contract по решению владельца** («оставь старый UUID — через версию удалим»): старая UUID-колонка сохранена для старых бинарей при раскатке, новая версия пишет/читает только `group_offset_seq`; падение UUID-колонки — отдельная контрактная версия |

**Курсор живёт на `msg_seq` везде** (`SqlLoadConsumerGroup`): предикат `msg_seq > @offset`, `ORDER BY msg_seq`,
`RETURNING msg_seq`, максимум пачки — `MAX(msg_seq)` возвращается как новый offset. Лоадер работает с `long`:
`group_offset_seq` читается через `GetInt64`, offset-параметр — `bigint`.

**Флоры (публичный API сохранён).** `WithMinOffset(string, Guid)` и `WithMinOffset(string, DateTimeOffset)`
остаются; `GetMinOffset` удалён. Каждый флор при первом использовании (внутри транзакции с advisory-локом)
разрешается в **эксклюзивную границу `msg_seq`** и кэшируется на группу:

```sql
-- флор по v7-id:  последний msg_seq, чей msg_id ещё сортируется ниже флора
SELECT COALESCE(MAX(msg_seq), 0)
FROM __msg$
WHERE tenant_id = @tnt AND msg_id < @msg_id;
-- флор по дате:  то же по msg_created_at (unix-секунды)
... AND msg_created_at < @flr_date;
```

Граница эксклюзивная (строгий `<`), а не инклюзивная: `MAX(...) WHERE key >= floor` дал бы off-by-one —
сообщение, ровно совпавшее с флором, было бы пропущено, а при пустой выборке `MIN` схлопнулся бы в 0
и доставил бы всё. Эксклюзивная форма сохраняет семантику старого строгого `msg_id > @offset` в точности.

**Upgrade.** Свежие БД получают полную схему из `All()`; существующие догоняются идемпотентными
`ADD COLUMN IF NOT EXISTS` из post-хука Partitional (строки `SqlAddMsgSeqColumnToMsgTable`,
`SqlAddMsgSeqColumnToTaskTable`, `SqlAddGroupOffsetSeqColumnToOffsetTable`) — повторный проход миграции
безопасен, на свежей БД это no-op. Хуки меняют только схему; **данные** наполненного деплоя
переносятся вручную:

```sql
-- 1) msg_seq на старых строках msg (если важен порядок: перенумеровать по msg_id,
--    а не полагаться на физический порядок бэкфилла BIGSERIAL);
-- 2) затравка нового курсора: иначе новая колонка = 0 и история пере-доставится целиком.
UPDATE __offset$ o
SET group_offset_seq = COALESCE((
  SELECT MAX(t.msg_seq) FROM outbox t
  WHERE t.consumer_group = o.consumer_group AND t.tenant_id = o.tenant_id
), 0);
```

**Что НЕ вошло:** детектор рассинхрона «вставлено 0 строк при `msg_created_at < now - batchingWindow`»
(п. «Дополнительно» выше) — отдельным решением; полная подсистема версионирования DDL из C14 —
пока закрыта идемпотентными хуками (см. C14).

**Тесты (зелёные, 2026-09-29):** `SqlOutboxBuilderTests` — пины на SQL-форму (`msg_seq > @offset`,
`RETURNING msg_seq`, обе колонки `__offset$`, апгрейд-SQL, флор-резолверы, COPY без `msg_seq`);
`DeliveryCursorMsgSeqTests.SecondPublish_WithIdsBelowCursor_IsStillDelivered` — скошенные часы
(пачка B с `msg_id` на 3 ч **раньше** пачки A всё равно доставлена, seq [1..5], курсор = 5);
`DeliveryMinOffsetFloorTests.FloorByDate_SkipsMessagesBeforeIt` — флор по дате пропускает сообщение
ниже границы и доставляет ровно одно. Полный прогон решения: 1351 тест, 0 failed.

**Находки по ходу:** (1) флор-резолвер был спроектирован как инклюзивный `MIN(seq) WHERE >= floor` —
схлопывание в 0 на пустой выборке (доставка всего) + off-by-one по самому полу; исправлено на
эксклюзивную границу до мержа. (2) В тесте флора `new DateTimeOffset(sec, TimeSpan.Zero)` трактует
секунды как **тики** (флор = год 0001 → всё доставлено); заменено на `DateTimeOffset.FromUnixTimeSeconds`.

---

## C3 — КРИТИЧНО (производительность, деградация со временем). Завершённые задачи не удаляются и не индексируются

**Где:** весь модуль — `grep 'DELETE|TRUNCATE' src/Sa.Outbox.PostgreSql` даёт **0 совпадений**.

`SqlFinishDelivery` (`SqlOutboxBuilder.cs:322-368`) делает только `INSERT` в `__log$` + `UPDATE` task-строки.
Строка task остаётся навсегда со статусом `200/201/204/...`; удаляется только целиком дневная партиция
по retention.

При этом фильтр выбора в `SqlLockAndSelect` (`:62-64`) — `delivery_status_code IN (0,100,103,104,400)`.
Завершённые статусы в него **не входят** ⇒ каждая задача, попадая в `LIMIT @lim` по `ORDER BY task_id`,
требует, чтобы планировщик её **пропустил**. Единственный индекс task-таблицы — PK `(task_id, task_created_at)`
(`Sa.Partitional.PostgreSql/SqlBuilder/SqlTemplate.cs:62-65`); вторичных индексов модуль не создаёт вовсе.

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
`Sa.Partitional.PostgreSql/SqlBuilder/SqlTemplate.cs:62,101,135`; вспомогательные `__type$` и `__offset$` —
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

**Статус 2026-09-29:** полная подсистема версионирования **не** построена. Для C2 использован
прагматичный поднабор — идемпотентные `ADD COLUMN IF NOT EXISTS`-хуки (`SqlAddMsgSeqColumnToMsgTable`,
`SqlAddMsgSeqColumnToTaskTable`, `SqlAddGroupOffsetSeqColumnToOffsetTable`), которые выполняются в
post-хуке `AddPostSql` (`Partitional/Setup.cs`) на каждом проходе миграции и безопасны при повторе.
Этого достаточно для *схемы*; перенос *данных* наполненного деплоя остаётся ручным документированным
шагом (см. C2). Версионирование DDL (`__schema$`-таблица с номером версии) — открытая задача на
следующую смену схемы.

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

## M1 — `StringBuilderPooledObjectPolicy` настроен так, что пул не работает вообще — ЗАКРЫТО (2026-09-29)

**Где:** `SqlBuilder/Setup.cs:15-23`

```csharp
var policy = new StringBuilderPooledObjectPolicy() { InitialCapacity = 1024 };
```

`MaximumRetainedSize` **не задан**, а дефолт в BCL — `4 * 1024 = 4096` байт. `BuildDeliveryInsertValues`
(`SqlOutboxBuilder.cs:370-401`) и `BuildErrorInsertValues` (`:284-310`) строят VALUES-список на строку чанка
(до 1024, см. M8/Q4) ≈ 30–120 КБ ⇒ **каждый** построитель больше 4 КБ ⇒ `objectPool.Return(sb)` молча его
выбрасывает, а `Get()` каждый раз выделяет новый `StringBuilder(1024)`, который затем 5–6 раз дорастает с копированием.

При этом пул на hot path **не используется вовсе**: `FinishDeliveryCommand.cs:11` и
`ErrorDeliveryCommand.cs:13` пропускают SQL через `SqlCacheSplitter`, который мемоизирует по длине —
`genSql` (а значит и `Build*InsertValues`) вызывается только при первом попадании ключа. То есть
~50 вызовов за процесс, и все они тратят пул, который ничего не возвращает.

**Исправление:** удалить `ObjectPool<StringBuilder>` целиком, в `Build*InsertValues` использовать
`new StringBuilder(EstimateCapacity(count))` (оценка: `count * 48`). Минус ~40 строк кода, исчезает
ложная «оптимизация». Альтернатива (если хочется оставить пул) — задать `MaximumRetainedSize = 128 * 1024`.

**Сделано:** пул удалён целиком — регистрации выпилены из `SqlBuilder/Setup.cs`, ctor
`SqlOutboxBuilder(PgOutboxTableSettings)` без `ObjectPool`; `BuildErrorInsertValues` — `new StringBuilder(count * 48)`,
`BuildDeliveryInsertValues` — `new StringBuilder(count * 96)` (8 индексированных + 3 общих имени на строку —
≈ 90–96 байт, оценка-флор, дорастание амортизировано). `SqlOutboxBuilderTests.Create()` обновлён.

---

## M2 — `WritePayload`: стрим на каждое сообщение + двойное копирование payload — ЗАКРЫТО (2026-09-29)

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

**Сделано (с поправкой на Npgsql 10):** реализована нижняя половина — один `RecyclableMemoryStream` на батч
(`WriteRows` берёт поток до цикла; `WritePayload` делает `SetLength(0)` → сериализация в тот же буфер →
`Position = 0`; `writer.Write(stream, Bytea)` читает ровно текущий payload). Span-вариант
`writer.Write(stream.GetBuffer().AsSpan(0, len), Bytea)` **невозможен**: у `NpgsqlBinaryImporter` в Npgsql 10.0.3
только generic `Write<T>(T, NpgsqlDbType)` (проверено по `Npgsql.xml` пакета, `Write(ReadOnlySpan<byte>)`
отсутствует) — write остался стрим-в-writer, но `GetStream` теперь 1 на батч вместо 1 на сообщение, и буфер
перематывается, а не пересоздаётся. Выигрыш «16 → 1» получен; «−1 копия payload» внутри Npgsql не подтверждается.

---

## M3 — `NpgsqlOutboxReader`: 17 lookup'ов по имени колонки на строку — ЗАКРЫТО (2026-09-29)

**Где:** `Commands/NpgsqlOutboxReader.cs:20-63`, вызывается из `StartDeliveryCommand.cs:53-111`

Все 17 аксессоров используют `reader.GetGuid(name)` / `GetString(name)` / `GetInt64(name)`.
Перегрузка `GetXxx(string name)` внутри делает `GetOrdinal(name)` — линейный поиск по массиву имён
с регистронезависимым сравнением. На батче в 16 сообщений это 272 lookup'а на один SELECT,
и они повторяются **на каждом опросе** каждой группы.

**Исправление:** один раз на команду построить `readonly record struct Ordinals(int TaskId, int TenantId, ...)`
(17 `int` = 68 байт, в стеке, без аллокаций), передавать её в ридер. `NpgsqlDataReader.GetOrdinal`
кешировать **нельзя** (reader переиспользуется), но кешировать ординалы один раз на `NpgsqlCommand` — можно и нужно.

Побочно: `NpgsqlOutboxReader` — `internal`, поэтому изменение сигнатуры не ломает публичный API.

**Сделано:** `TaskQueueReader.Ordinals` — `readonly record struct` из 14 `int` + статический `Capture`
(по `TableFields`, `reader.GetOrdinal`); все 14 аксессоров `TaskQueueReader` переведены на `Get*(reader, in Ordinals)`.
`StartDeliveryCommand.ExecuteFill` захватывает ординалы на первой строке через замыкание (`bool haveOrdinals`):
команда — singleton, но колбэк `ExecuteReader` синхронный на строку и локали живут в замыкании вызова, поэтому
гонок нет. `MsgReader`/`TypeReader` (однострочные чтения, 3 аксессора) намеренно не тронуты.

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

**Статус:** отложен по дизайну — единственный оставшийся пункт этапа 4; требует расширения публичного
`IOutboxMessageSerializer` (R3) и обоснован профилировщиком: средний риск против небольшого выигрыша
на фоне уже закрытых M2/M3.

---

## M5 — `ErrorDeliveryCommand`: текст ошибки собирается дважды — ЗАКРЫТО (2026-09-29)

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

**Сделано:** `ErrorRow` расширен до `(Exception Exception, string ErrorMessage, ErrorInfo Info)`;
`GroupByException` композирует `GetErrorMessage` один раз, хеширует готовую строку
(`errorMessage.GetMurmurHash3()`) и кладёт её в `ErrorMessage`; `Fill` пишет `row.ErrorMessage`.
`GetErrorMessageHash(Exception)` оставлен как тест-пин (в производственном пути больше не используется).

---

## M6 — `OutboxPartInfo[]` + `HashSet` на каждый publish — ЗАКРЫТО (2026-09-29)

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

**Сделано:** `OutboxPartRepository.EnsureParts` дедуплицирует по `(StartOfDay(CreatedAt), TenantId, Part)` —
`HashSet` на вызов вместо `Distinct()` по record'у с точным `CreatedAt` (в `ReturnDelivery` ключи уже приходят
дедуплицированными из M7; набор здесь — защита для остальных путей, `List.Contains` из черновика не понадобился).
Параллельность: `EnsureMsgParts`+`EnsureTaskParts` в rent-пути и `EnsureDeliveryParts`+`EnsureErrorParts` в
`ReturnDelivery` — оба плеча `Task.WhenAll` (независимые DDL на разных таблицах).

---

## M7 — `ReturnDelivery`: три линейных прохода с аллокациями по батчу — ЗАКРЫТО (2026-09-29)

**Где:** `Services/Plug/OutboxDeliveryManager.cs:37-91`

```csharp
var parts = messages.Span
    .SelectWhere(c => c.DeliveryInfo.PartInfo with { CreatedAt = c.DeliveryResult.CreatedAt })  // :44-47 массив
    .Distinct();                                                                                // HashSet
...
IOutboxContextOperations<TMessage>[] errs = messages.Span.SelectWhere(m => m, m => ...);      // :78-80 массив
var dates = errs.Select(c => c.DeliveryResult.CreatedAt);                                     // :85 IEnumerable, аллоцируется лениво
```

Три отдельных прохода, три аллокации на батч в 16–4096 элементов (чанки до `DefaultMaxLen`). `dates` затем ещё раз
`.StartOfDay().Distinct()` в `OutboxPartRepository.cs:24`. Объединить в один проход,
собирая дедуплицированные `day`-ключи в один переиспользуемый `HashSet<(int,string,DateTimeOffset)>`.

**Сделано:** `ReturnDelivery` — один `foreach` по `messages.Span`: `List<IOutboxContextOperations<TMessage>>? errs`
(лениво, через `??=`), `HashSet<OutboxPartInfo>` delivery-партиций (с `StartOfDay` в `with`, M6) и
`HashSet<DateTimeOffset>` error-дней. Затем два ensure-таска через `Task.WhenAll` (M6) и один
`errorCmd.Execute(errs.ToArray(), ct)` — ковариация массива в `ReadOnlyMemory<IOutboxContext>` (тот же вызов, что
и раньше, теперь с одним массивом вместо трёх структур). `GetErrors` удалён.

---

## M8 — `CachedParamNames<T>` держит 4608 строк всегда — ЗАКРЫТО (2026-09-29)

**Где:** `Sa.Data.PostgreSql/DbCommandExtensions.cs:57-101`, `Commands/NpgsqlCommandExtension.cs:142-158`

`CreateCachedArrays(9 префиксов, MaxIndex)` = **9 × MaxIndex объектов `string`** + 9 массивов, живущих весь
процесс (~200–500 КБ вместе со ссылками), плюс `ReadOnlyDictionary` поверх `Dictionary`.

Плюс: `MaxIndex` в `BatchParams` и `maxLen` в `SqlCacheSplitter.GetSql` — **два несвязанных литерала**, а
комментарий в `SqlCacheSplitter.cs` («~16 SQL параметров на элемент») фактически неверен: в `SqlFinishDelivery`
на строку приходится 8 индексированных + 4 общих параметра, в `SqlError` — 4. Замер на реальном SQL закреплён
пином `SqlOutboxBuilderTests.BulkStatements_ParameterPerRowRatio_Is8And4_Not16` (4100 и 2048 параметров при
512 строках).

**Исправление (сделано):**
- единая `public const int SqlCacheSplitter.DefaultMaxLen = 1024` + `BatchParams.MaxIndex => DefaultMaxLen`
  (связка констант — не косметика, а инвариант: индекс ≥ MaxIndex тихо падает на интерполяцию имён
  параметров; общая константа делает расхождение невозможным по построению; связка закреплена тестом
  `SqlCacheSplitterTests.DefaultMaxLen_Is1024_AndDrivesTheParameterNameCache`);
- комментарий переписан на фактические 8+4/N и 4 параметра на строку (выбор значения — Q4);
- кеш имён (9216 строк при 1024) задокументирован в `DbCommandExtensions` — «разменная монета против
  интерполяции на каждый параметр», чтобы следующий читатель не удалил «лишний» кеш.

---

## M9 — `SqlOutboxBuilder`: 12 интерполированных строки на инстанс — ЗАКРЫТО (2026-09-29)

**Где:** `SqlBuilder/SqlOutboxBuilder.cs:25-269`

Поля объявлены как `public readonly string` **экземплярные**, т.е. собираются в конструкторе. Регистрация —
`TryAddSingleton` (`SqlBuilder/Setup.cs:25`), поэтому выполняется один раз. Замечание только на будущее:
любой переход на `Scoped`/`Transient` даст ~12 строк на резолв. Если планируется, вынести в
`static readonly` + кеш по `(schema, tableNames)` — либо оставить комментарий, что класс обязан быть singleton.

**Сделано:** XML-remark на классе `SqlOutboxBuilder` — «зарегистрирован singleton (`SqlBuilder/Setup.cs`), шаблоны
строятся в конструкторе из настроек; смена регистрации на scoped/transient = пересборка ~20 шаблонов на резолв —
требует выноса шаблонов в static + кеша по (schema, table)».

---

## Сводная таблица оптимизаций

| # | Что | Место | Эффект | Риск |
|---|---|---|---|---|
| M1 | Убрать мёртвый `ObjectPool<StringBuilder>` | `SqlBuilder/Setup.cs:15-23` | ~40 строк кода, -1 класс, пул не работал | нулевой |
| M2 | 1 стрим на батч (span-оверлоада в Npgsql 10 нет) | `BulkInsertMsgCommand.cs:63-100` | 16→1 аллокация; −1 копия не подтверждена | низкий |
| M3 | Кеш ординалов колонок | `NpgsqlOutboxReader.cs` | −17 lookup/строку → 0 | низкий |
| M5 | Текст ошибки один раз | `ErrorDeliveryCommand.cs:60,124` | −50% CPU на `__error$` | нулевой |
| M6 | Партиционный ключ без `CreatedAt` + параллельный `EnsureParts` | `OutboxBulkWriter.cs:18`, `OutboxPartRepository.cs:36-53` | −3 аллокации/publish, −N round trips | низкий |
| M7 | Один проход вместо трёх | `OutboxDeliveryManager.cs:44-85` | −3 аллокации/батч | нулевой |
| M4 | `Deserialize` из спана вместо `GetStream` | `StartDeliveryCommand.cs:98-103` | −1 объект/сообщение | средний (нужен новый API сериализатора) |
| M8 | Единая константа `DefaultMaxLen=1024`, честный комментарий (8+4/N, 4) | `NpgsqlCommandExtension.cs:142`, `SqlCacheSplitter.cs` | гигиена + подъём потолка чанка (Q4): 1024-батчи 2→1 round trip | нулевой |
| M9 | Комментарий «обязан быть singleton» | `SqlOutboxBuilder.cs` | предотвращает регрессию | нулевой |

Статусы на 2026-09-29: **закрыты M1, M2, M3, M5, M6, M7, M8, M9**; **M4 отложен** (нужен новый API
`IOutboxMessageSerializer`, см. R3). `Sa.Outbox.PostgreSqlTests` — 104/104 зелёные.

---

# Часть III. Что делать и в каком порядке

## Этап 1 — корректность (без изменения схемы) — выполнено 2026-09-28

1. **C5** — логировать несовпадение `affected < batch` в `FinishDeliveryCommand`. 3 строки. ✓
   (`FinishDeliveryCommand.cs:48-57` + `LogStolenBatch`, Warning, EventId 3008; тест `DeliveryStolenBatchTests`)
2. **C4** — `SELECT DISTINCT tenant_id`. 1 строка, максимальный эффект на серверную память. ✓
   (`SqlSelectTenant`; тест плана `SqlSelectTenantPlanTests`)
3. **C6** — убрать/осмыслить два `ON CONFLICT DO NOTHING`. ✓ (`__log$`-вставка без `ON CONFLICT`,
   комментированная в `SqlFinishDelivery`; `ON CONFLICT` остался только там, где он load-bearing — `SqlInsertType`)
4. **C11**, **C9**, **C12** — мелочи, чистка. ✓ (`SqlInsertOffset` удалён как мёртвый код;
   `delivery_attempt`: исключение для `Postpone` (103) закреплено комментарием-дизайн-решением;
   `delivery_created_at = 0` → `DateTimeOffset.MinValue` в `NpgsqlOutboxReader`)
5. **C8** — переименовать параметры + комментарий-таблица семантики. ✓ (`@win_from`/`@win_to`/`@now`,
   таблица семантики — в шапке `SqlOutboxBuilder.cs:57-71`; тест `DeliveryBatchingWindowTests`)

## Этап 2 — индексы (решает C3)

6. Частичный индекс `ix_{task}_live` через существующий хук `ITableBuilder`/`ITableSettings`
   (образец — в doc-комментарии `SqlRootBuilder.cs:6`). Учесть `CREATE INDEX CONCURRENTLY`.
7. **[не проверено]** `EXPLAIN (ANALYZE, BUFFERS)` на `SqlLockAndSelect` до/после. Ожидание:
   `Seq Scan`/`Index Scan` с большим `Rows Removed by Filter` → `Index Scan` по `ix_{task}_live`.

## Этап 3 — согласованные решения (Q1–Q3 закрыты 2026-09-28)

8. **C14 + C15 (первыми)** — тесты, падающие на текущем коде, и подсистема миграций DDL.
   Без них пункты 9–11 недоказуемы: нечем подтвердить, что фикс что-то исправил.
   → C15 закрыт тестами C2 (2026-09-29); C14 закрыт частично (идемпотентные ADD COLUMN-хуки, см. C14).
9. **C1** — `DELETE` зависших задач в том же statement + счётчик в лог. *(Q1: вариант a)*
10. **C2** — `msg_seq BIGSERIAL` на `__msg$`, курсор переезжает на `msg_seq`,
    `__offset$`: легаси `group_offset` UUID + живой `group_offset_seq` BIGINT (expand).
    Публичный API `WithMinOffset` сохраняется. *(Q2: вариант 3)* — **реализовано 2026-09-29**
    (пины SQL-формы + 2 интеграционных теста скошенных часов и флора; полный прогон 1351/0).
11. **C3 (удаление)** — `DELETE` успешно завершённых task-строк с сохранением `task_id` в `__log$`.
    Перед удалением — `ANALYZE` партиции. *(Q3: да)*

**Порядок внутри этапа 3 жёсткий:** сначала C15-тесты (красные), потом C14-подсистема миграций,
затем C2. C1 и удаление C3 можно делать параллельно — они не зависят ни от C2, ни от C14.

## Этап 4 — память

12. M1, M5, M7, M8, M3, M2, M6 — независимы, каждый мержится отдельно.
13. M4 — только если профилировщик покажет, что `GetStream`/`Deserialize` доминируют.

## Тесты, которые обязательны для этапов 1–3

- `C1`: задача без сообщения (удалить строку из `msg$`) → consumer group продолжает доставлять. — **открыто**
- `C2`: две publish-пачки в пределах одной мс; backdated-публикация; два инстанса со сдвинутыми часами
  (через подменённый `TimeProvider`) → ни одного потерянного сообщения.
  — **backdated/сдвинутые часы закрыты** (`DeliveryCursorMsgSeqTests.SecondPublish_WithIdsBelowCursor_IsStillDelivered`,
  `DeliveryMinOffsetFloorTests.FloorByDate_SkipsMessagesBeforeIt`, оба 2026-09-29);
  **две пачки в пределах одной мс — остаётся открытым** (последовательный порядок через seq покрыт,
  но явного same-millisecond-теста нет).
- `C3`: 50k завершённых задач в партиции + план `SqlLockAndSelect` — время и buffers не растут. — **открыто**
- `C5`: батч, чей `LockDuration` истёк во время `Consume` → в логе `Warning`, повторная доставка. — **закрыт** (`DeliveryStolenBatchTests`)
- `C8`: `BatchingWindow = 0` → захват задачи сразу, без сдвига. — **закрыт** (`DeliveryBatchingWindowTests`)

---

# Решения и открытые вопросы

## Закрыто 2026-09-28

**Q1. Задача без сообщения (C1)?** → **`DELETE` в том же statement + счётчик и предупреждение в лог.**
Вариант (b) с записью в `__error$` отклонён: он требует `EnsureErrorParts` в `RentDelivery` (+1 round trip
на каждую выборку) ради записи, которая по определению описывает внутреннюю рассинхронизацию, а не
сбой доставки. Вариант (c) отклонён: снимает блокировку, но строки копятся и C3 остаётся нерешённым.

**Q2. Модель курсора (C2)?** → **вариант 3: `msg_seq BIGSERIAL` на `__msg$`.** Разбор всех четырёх
вариантов и разбор радиуса поражения — в секции C2. Публичный API `WithMinOffset` сохраняется.
**Реализован 2026-09-29** (см. «Реализовано 2026-09-29» в C2): окончательная форма — expand по
`__offset$` (легаси `group_offset` UUID + живой `group_offset_seq` BIGINT, UUID-колонка убирается
отдельной контрактной версией), флоры транслируются в эксклюзивную границу `msg_seq` и кэшируются;
`GetMinOffset` удалён.

**Q3. Удалять ли успешно завершённые строки task (C3)?** → **да**, с `task_id` в `__log$` (уже есть,
`SqlOutboxBuilder.cs:334`) и `ANALYZE` партиции. Потеря «поиска по `task_id` в живой таблице» приемлема:
`__log$` — каноническое место для завершённых задач.

**Миграции DDL.** Отдельным решением: заводим подсистему версионирования (C14). Ручной релизный скрипт
как постоянный механизм отклонён — `CREATE TABLE IF NOT EXISTS` в `SqlTemplate.cs` и `SqlOutboxBuilder.cs`
гарантирует, что без подсистемы следующая же смена схемы снова упрётся в стену.

## Открыто — нужно ваше решение

**Q4. Поднимать ли потолок батча (M8)?** — **РЕШЕНО 2026-09-29: поднят до 1024** (`SqlCacheSplitter.DefaultMaxLen`,
одна константа на оба места, см. M8).

Проверка эффективности по реальному SQL (а не по мифическим «~16 параметров»): на строку `SqlFinishDelivery`
приходится 8 индексированных + 4 общих параметра, `SqlError` — 4 (пин `BulkStatements_ParameterPerRowRatio_Is8And4_Not16`).
При 512 строках finish-чанк связывает 8×512+4 = **4100** параметров (6.3% лимита 65535), error — **2048** (3.1%);
потолок одного statement — (65535−4)/8 ≈ **8190** строк. Т.е. 512 оставлял ~16× неиспользованного запаса и стоил
потребителям `MaxBatchSize=1024` (shipped-пресет, реально гоняется в `OutboxParallelMessagingTests`) **два** round
trip'а за цикл доставки. При 1024: 8×1024+4 = **8196** параметров (12.5% лимита, 8× запас), пресет 1024 — один
statement. Цена подъёма: кеш имён параметров 4608→9216 строк и SQL-кеш ≤47→79 ключей — доли МБ. 4096 (50% лимита,
кеш ~2 МБ) не выбран: выигрыш только для батчей >1024, которых нет в shipped-пресетах; 8192 уже не влезает
(65540 > 65535).

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

**Статус 2026-09-29 — expand/contract применён к `__offset$`:** обе колонки (`group_offset` UUID и
`group_offset_seq` BIGINT) живут одновременно, старые бинари продолжают читать UUID-колонку при
раскатке, контрактное удаление — отдельной версией. Для `__msg$`/`__task$` `ADD COLUMN IF NOT EXISTS`
выполняется идемпотентным хуком автоматически (на свежих БД — no-op), но бэкфилл значений на
наполненной БД — ручной документированный шаг (SQL в C2, «Реализовано 2026-09-29»). Одномоментная
миграция с переписыванием данных не используется.

---

# Статус реализации (хронология)

Всё ниже — изменения, внесённые по этому документу. Git-состояние: правки не коммитились, рабочее
дерево не тронуто командами git.

## 2026-09-28 — этап 1: корректность без изменения схемы

| Пункт | Что сделано | Где |
|---|---|---|
| C4 | `SqlSelectTenant` — `SELECT DISTINCT tenant_id` вместо оконной функции | `SqlBuilder/SqlOutboxBuilder.cs` (`SqlSelectTenant`) |
| C5 | Учёт «украденного» батча: `total != messages.Length` → `LogStolenBatch` (Warning, EventId 3008) | `Commands/FinishDeliveryCommand.cs:48-57` |
| C6 | Убран бессмысленный `ON CONFLICT DO NOTHING` в `SqlFinishDelivery`; оставлен только там, где он load-bearing (`SqlInsertType`) | `SqlBuilder/SqlOutboxBuilder.cs` |
| C8 | Семантика параметров окна разведена: `@win_from`/`@win_to`/`@now` + таблица семантики в шапке класса | `SqlBuilder/SqlParam.cs`, `SqlBuilder/SqlOutboxBuilder.cs:57-71` |
| C9 | `delivery_attempt` — поведение подтверждено как дизайн-решение: инкремент на всех кодах, кроме `Postpone` (103); исключение закреплено комментарием «do not fix» | `SqlBuilder/SqlOutboxBuilder.cs:416-421` |
| C11 | `SqlInsertOffset` удалён как мёртвый код (побайтовый дубликат `SqlInitOffset`, вдобавок синтаксически битый) | `SqlBuilder/SqlOutboxBuilder.cs` |
| C12 | `delivery_created_at = 0` → `DateTimeOffset.MinValue` вместо `1970-01-01` | `Commands/NpgsqlOutboxReader.cs:72` |

Тесты этапа 1 (все зелёные): `SqlSelectTenantPlanTests` (план `DISTINCT`), `DeliveryStolenBatchTests`
(перехват батча → Warning + повторная доставка), `DeliveryBatchingWindowTests` (`BatchingWindow = 0`),
`CleanupSettingsDefaultsTests`, `OutboxIdGeneratorTests` — плюс существующий набор
(`ErrorDeliveryGroupingTests`, `SqlCacheSplitterTests[Concurrency]`, `Delivery*`-набор, `OutboxTests`,
`OutboxTwoGroupsTests`, `OutboxTenantParallelismTests`, `OutboxPublisherTests`,
`OutboxParallelMessagingTests`).

## 2026-09-29 — C2: курсор на `msg_seq` (вариант 3, финальный состав)

Полный состав — в секции C2 («Реализовано 2026-09-29»). Коротко:

- **Схема**: `__msg$` `msg_seq BIGSERIAL NOT NULL` (порядок назначает база; COPY колонку не перечисляет);
  `outbox` `msg_seq BIGINT NOT NULL DEFAULT 0` (денормализовано — чтобы `RETURNING msg_seq` работал);
  `__offset$` — expand: легаси `group_offset` UUID + живой `group_offset_seq BIGINT NOT NULL DEFAULT 0`.
- **Курсор**: `SqlLoadConsumerGroup` (INSERT/SELECT/`ORDER BY`/`RETURNING`/`MAX`) — на `msg_seq`;
  лоадер читает `long` (`GetInt64`), `AddParamOffset(long)`, `IOutboxTaskLoader.LoadGroupResult(int, long)`.
- **Флоры**: `WithMinOffset(Guid/DateTimeOffset)` сохранены, внутренний словарь — сырые флоры +
  кэш разрешённых границ; резолвер — `COALESCE(MAX(msg_seq),0) WHERE tenant_id=@tnt AND key < @floor`
  (**эксклюзивная** граница: off-by-one инклюзивного `MIN >= floor` был пойман до мержа); `GetMinOffset` удалён.
- **Upgrade**: три идемпотентных `ADD COLUMN IF NOT EXISTS`-хука в post-хуке `Partitional/Setup.cs`
  (msg, task, offset); перенос данных наполненного деплоя — ручной документированный шаг (SQL в C2).
- **Публичный API**: без изменений (перегрузки `WithMinOffset` сохранены; курсор внутри — BIGINT).

Тесты (зелёные): `SqlOutboxBuilderTests` — пины на SQL-форму (обе колонки `__offset$`, `SELECT group_offset_seq`,
`msg_seq>@offset` / `,msg_seq` / `msg_id,\n msg_seq`, `RETURNING msg_seq`, апгрейд-SQL, флор-резолверы,
`SqlBulkMsgCopy_DoesNotListMsgSeq`); `DeliveryCursorMsgSeqTests.SecondPublish_WithIdsBelowCursor_IsStillDelivered`
(пачка B с id на 3 ч раньше пачки A доставлена; seq [1..5]; курсор 5); `DeliveryMinOffsetFloorTests.FloorByDate_SkipsMessagesBeforeIt`
(флор по дате: сообщение ниже границы пропущено, доставлено одно). Полный прогон решения — **1351 тест, 0 failed, 5 skipped**.

## 2026-09-29 — M8/Q4: потолок батча 512 → 1024

Проверка эффективности батча 512 (по запросу): замер реального SQL показал, что на строку `SqlFinishDelivery`
приходится 8 индексированных + 4 общих параметра (4100 при 512 строках, 6.3% лимита 65535), `SqlError` — 4 (2048,
3.1%), потолок одного statement — ~8190 строк. Поток «~16 параметров на элемент» был неверен. Решение: поднять
потолок чанка до 1024 единой константой — 1024-пресет (shipped, гоняется в `OutboxParallelMessagingTests`) больше
не режется на два чанка, 8× запас до лимита сохраняется.

- `Commands/SqlCacheSplitter.cs`: `public const int DefaultMaxLen = 1024` (+ честные комментарии: реальные
  соотношения 8+4/N и 4, кеш ≤79 ключей, worst-case несколько МБ SQL-текста против прежних «~50 КБ»);
- `Commands/NpgsqlCommandExtension.cs`: `BatchParams.MaxIndex => SqlCacheSplitter.DefaultMaxLen` (связка констант —
  инвариант против тихой деградации кеша имён; `BatchParams` стал `internal` для теста);
- `Sa.Data.PostgreSql/DbCommandExtensions.cs`: кеш имён параметров задокументирован (9 префиксов × MaxIndex =
  9216 строк при 1024);
- пины: `SqlOutboxBuilderTests.BulkStatements_ParameterPerRowRatio_Is8And4_Not16` (4100/2048),
  `SqlCacheSplitterTests.DefaultMaxLen_Is1024_AndDrivesTheParameterNameCache`,
  `GetSql_DefaultMaxLen_1024BatchIsOneChunk_1025Splits`; инвариант-тест сплиттера переведён на `DefaultMaxLen`,
  арифметические тесты — на явный `maxLen: 512` (алгоритм, а не значение).

## 2026-09-29 — M1–M9: этап 4 оптимизаций закрыт

Закрыты M1, M2, M3, M5, M6, M7, M9 (M8 — блок выше в этой же дате; M4 отложен по дизайну — нужен новый API
`IOutboxMessageSerializer`, см. R3). Полный прогон `Sa.Outbox.PostgreSqlTests` — 104/104, затем полный прогон решения.

- **M1** — мёртвый `ObjectPool<StringBuilder>` выпилен (`SqlBuilder/Setup.cs`, ctor `SqlOutboxBuilder`);
  `BuildErrorInsertValues` → `count*48`, `BuildDeliveryInsertValues` → `count*96`;
- **M2** — один `RecyclableMemoryStream` на батч: `WritePayload` перематывает буфер (`SetLength(0)`), а не создаёт
  стрим на сообщение. Span-запись невозможна — у `NpgsqlBinaryImporter` в Npgsql 10.0.3 только generic `Write<T>`
  (проверено по `Npgsql.xml` пакета);
- **M3** — `TaskQueueReader.Ordinals` (14 `int`, `readonly record struct`) + статический `Capture`; захват на
  первой строке в `StartDeliveryCommand.ExecuteFill`, чтение по индексу;
- **M5** — `ErrorRow(Exception, string ErrorMessage, ErrorInfo)`; текст композируется один раз в `GroupByException`,
  хеш — от готовой строки; `Fill` пишет `row.ErrorMessage`;
- **M6** — `EnsureParts` дедуплицирует по `(StartOfDay(CreatedAt), TenantId, Part)`;
  rent-путь: msg+task `Task.WhenAll`; `ReturnDelivery`: delivery+error `Task.WhenAll`;
- **M7** — `ReturnDelivery` — один проход: errs-список, delivery-партиции и error-дни собираются разом,
  `GetErrors` удалён;
- **M9** — XML-remark «обязан быть singleton» на `SqlOutboxBuilder`.

## Осталось (открытые пункты)

- C1 — DELETE зависших задач в том же statement (Q1, вариант a) + тест;
- C3 — удаление завершённых task-строк + частичный индекс `ix_{task}_live` (этап 2) + `EXPLAIN`-бенч (Q5);
- полная подсистема версионирования DDL (C14) — сейчас закрыта идемпотентными хуками;
- явный same-millisecond-тест для C2 (две пачки в одну мс);
- M4 — отложен по дизайну (нужен новый API `IOutboxMessageSerializer`, R3); M1–M3, M5–M9 закрыты (2026-09-29).
